// =====================================================================================
//  AlignToConnector.cs   (Align to Connector: engine + floating palette + remembered segment)
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using TextBox = System.Windows.Controls.TextBox;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.ExternalService;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace AlignToConnector
{
    // ------------------------------------------------------------------------------
    //  Configuration
    // ------------------------------------------------------------------------------
    internal static class Cfg
    {
        public const double MmToFt = 1.0 / 304.8;

        /// <summary>Minimum geometric length an adjacent run (fuse) must retain.</summary>
        public const double MinNeighbourLengthMm = 1.0;

        public const double FocusMarginMinMm = 500.0;        // Automatic focus margin limits
        public const double FocusMarginMaxMm = 5000.0;
        public const double FocusMarginFactor = 0.4;
        public const double AlignTolFt = 0.1 * MmToFt;       // Below this threshold, considered "already aligned"
        public const double SkewTolFt = 0.5 * MmToFt;        // Lateral deviation tolerated on a neighbour (0.5 mm; above this the neighbour moves as a whole)
        public const double SweepEndShrinkMm = 5.0;          // Shortens the sweep solid at both ends

        public const double GuideMinHalfLengthM = 20.0;      // Cross guide is at least ±20 m
        public const double GuideExtraMarginM = 5.0;         // Margin over maximum distance to targets

        /// <summary>Categories tested for sweep collision.</summary>
        public static readonly BuiltInCategory[] CollisionCategories =
        {
            BuiltInCategory.OST_MechanicalEquipment,
            BuiltInCategory.OST_ElectricalEquipment,
            BuiltInCategory.OST_ElectricalFixtures,
            BuiltInCategory.OST_PlumbingFixtures,
            BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_PipeFitting,
            BuiltInCategory.OST_PipeAccessory,
            BuiltInCategory.OST_Conduit,
            BuiltInCategory.OST_ConduitFitting,
            BuiltInCategory.OST_DuctCurves,
            BuiltInCategory.OST_DuctFitting,
            BuiltInCategory.OST_DuctAccessory,
            BuiltInCategory.OST_CableTray,
            BuiltInCategory.OST_CableTrayFitting,
            BuiltInCategory.OST_StructuralFraming,
            BuiltInCategory.OST_StructuralColumns,
            BuiltInCategory.OST_GenericModel
        };

        /// <summary>Fittings that MOVE with the run (elbows, tees, unions).</summary>
        public static readonly BuiltInCategory[] FittingCategories =
        {
            BuiltInCategory.OST_PipeFitting,
            BuiltInCategory.OST_ConduitFitting
        };
    }

    // ------------------------------------------------------------------------------
    //  Data Models
    // ------------------------------------------------------------------------------

    /// <summary>EqualizeY = 0° guide (equalizes Y, keeps X and Z). EqualizeX = 90° guide (equalizes X, keeps Y and Z).</summary>
    internal enum AlignAxis { EqualizeY, EqualizeX, Auto }

    internal enum FrontierPolicy { Disconnect, MoveWith }

    internal enum ConflictKind
    {
        Frontier,          // Chain touches a boundary family (box, equipment, device...)
        NoSlack,           // An adjacent run cannot absorb the movement
        Skew,              // An adjacent run would end up skewed
        Collision,         // Sweep collides with another element
        RevitRejected,     // Revit rejected the change
        InvalidGeometry    // Geometry cannot be evaluated
    }

    internal enum AlignRequest { Retry, DisconnectAndAlign, MoveWithFamily, MoveChain, Cleanup }

    internal sealed class ConflictInfo
    {
        public ConflictKind Kind;
        public XYZ Point;
        public ElementId CurveId;
        public ElementId ConflictElementId = ElementId.InvalidElementId;
        public HashSet<ElementId> ChainIds = new HashSet<ElementId>();
        public string Message;
        public int RiskCount;        // Domino effect: branches that would compromise another fixed element
        public string RiskText = "";
    }

    internal sealed class DominoRisk
    {
        public Connector OnFamily;     // Boundary family connector going into the compromised branch
        public Connector FirstOther;   // Immediate connector on the other side (for DisconnectFrom)
        public Element Blocker;
        public XYZ Point;
        public string Reason;
    }

    internal sealed class FrontierInfo
    {
        public Element Family;
        public Connector ChainSide;    // Connector on the chain side (pipe/conduit or fitting)
        public Connector FamilySide;   // Connector on the family
        /// <summary>Every chain/family connector pair (a family can touch the chain through several connectors).</summary>
        public List<Connector[]> Pairs = new List<Connector[]>();
        public List<DominoRisk> Risks = new List<DominoRisk>();
    }

    /// <summary>Joint between a moving piece (run or elbow) and an adjacent straight run.</summary>
    internal sealed class ChainJoint
    {
        public ElementId MoverId;        // Run or fitting that moves
        public int MoverConnId;
        public ElementId NeighbourId;    // Adjacent straight run that absorbs the movement
        public int NeighbourConnId;
        public XYZ Origin;               // Joint position before moving
    }

    internal sealed class ChainResult
    {
        public MEPCurve Curve;
        public XYZ Displacement = XYZ.Zero;
        public bool AlreadyAligned;
        public HashSet<ElementId> MoverIds = new HashSet<ElementId>();   // Run + elbows / tees moving with it
        public List<ChainJoint> Joints = new List<ChainJoint>();
        public HashSet<ElementId> ChainIds = new HashSet<ElementId>();
        public HashSet<ElementId> FrontierIds = new HashSet<ElementId>();
        public List<FrontierInfo> Frontiers = new List<FrontierInfo>();
        // Links between the chain and the PIVOT family (detached on align; pivot never moves)
        public List<KeyValuePair<Connector, Connector>> PivotLinks = new List<KeyValuePair<Connector, Connector>>();
        public ConflictInfo Conflict;

        public ConflictInfo Fail(ConflictKind kind, XYZ point, ElementId element, string msg)
        {
            Conflict = new ConflictInfo
            {
                Kind = kind,
                Point = point,
                CurveId = Curve.Id,
                ConflictElementId = element ?? ElementId.InvalidElementId,
                ChainIds = ChainIds,
                Message = msg
            };
            return Conflict;
        }
    }

    internal sealed class AlignSession
    {
        public Document Document;
        public ElementId PivotId;
        public int PivotConnectorId = -1;
        public XYZ PivotPointOverride;
        public AlignAxis Axis;
        public List<ElementId> TargetIds = new List<ElementId>();
        public int NextIndex;
        public bool ForceRigid;      // Retry: all conduits in the chain move as a whole (without stretching)
        public string Note;          // Notice when a segment is skipped without moving (so it is not silent)

        /// <summary>User decision per boundary family.</summary>
        public Dictionary<ElementId, FrontierPolicy> Policies = new Dictionary<ElementId, FrontierPolicy>();

        /// <summary>"Move Chain Elements": the whole chain moves rigidly; boundary families are detached, collisions ignored.
        /// Applies only to the segment currently in conflict (reset as soon as it is processed).</summary>
        public bool MoveChainMode;

        /// <summary>"Disconnect and Align" on a non-boundary conflict: every connected family is detached from the chain.</summary>
        public bool DisconnectAll;

        public ConflictInfo Conflict;

        // Saved previous state for restoration
        public ElementId ViewId = ElementId.InvalidElementId;
        public Dictionary<ElementId, OverrideGraphicSettings> SavedOverrides =
            new Dictionary<ElementId, OverrideGraphicSettings>();
        public bool SectionSaved;
        public bool PrevSectionActive;
        public BoundingBoxXYZ PrevSectionBox;
        public bool VisualsActive;
    }

    // ------------------------------------------------------------------------------
    //  Temporary Keyboard Hook: Enter or Space Confirms Selection by Sending Esc
    // ------------------------------------------------------------------------------
    internal sealed class KeyConfirmHook : IDisposable
    {
        private const int WH_KEYBOARD = 2;
        private const uint WM_KEYDOWN = 0x0100;
        private const int VK_RETURN = 0x0D;
        private const int VK_SPACE = 0x20;
        private const int VK_ESCAPE = 0x1B;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private readonly IntPtr _hook;
        private readonly HookProc _proc;
        public volatile bool Enabled = true;
        public volatile bool Confirmed;

        public KeyConfirmHook()
        {
            _proc = Callback;
            _hook = SetWindowsHookEx(WH_KEYBOARD, _proc, IntPtr.Zero, GetCurrentThreadId());
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && Enabled)
            {
                int vk = wParam.ToInt32();
                bool isKeyDown = (lParam.ToInt64() & 0x80000000L) == 0;
                if (isKeyDown && (vk == VK_RETURN || vk == VK_SPACE))
                {
                    Confirmed = true;
                    try
                    {
                        IntPtr hwnd = Process.GetCurrentProcess().MainWindowHandle;
                        if (hwnd != IntPtr.Zero)
                            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)VK_ESCAPE, IntPtr.Zero);
                    }
                    catch { }
                    return (IntPtr)1;
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
                UnhookWindowsHookEx(_hook);
        }
    }

    // ------------------------------------------------------------------------------
    //  Selection Filter: Accepts Pipe or Conduit
    // ------------------------------------------------------------------------------
    internal sealed class MepCurveSelectionFilter : ISelectionFilter
    {
        private readonly ElementId _exclude;
        public MepCurveSelectionFilter(ElementId exclude) { _exclude = exclude; }

        public bool AllowElement(Element e)
        {
            if (e.Id.Equals(_exclude)) return false;
            if (AlignEngine.IsPipe(e)) return true;
            if (AlignEngine.IsConduit(e)) return true;
            return false;
        }
        public bool AllowReference(Reference r, XYZ p) { return false; }
    }

    // ------------------------------------------------------------------------------
    //  DirectContext3D Server: Ghost Cross (Does Not Create Elements in the Model)
    // ------------------------------------------------------------------------------
    internal sealed class GuideServer : IDirectContext3DServer
    {
        private readonly Guid _id = Guid.NewGuid();
        private readonly XYZ _origin;
        private readonly double _half;
        private MultiServerService _service;

        private VertexBuffer _vb;
        private IndexBuffer _ib;
        private VertexFormat _vf;
        private EffectInstance _ei;
        private int _lineCount;
        private bool _built;

        public GuideServer(XYZ origin, double halfLengthFt)
        {
            _origin = origin;
            _half = halfLengthFt;
        }

        public void Register()
        {
            var svc = ExternalServiceRegistry.GetService(
                ExternalServices.BuiltInExternalServices.DirectContext3DService);
            _service = svc as MultiServerService;
            if (_service == null) return;

            _service.AddServer(this);
            IList<Guid> ids = _service.GetActiveServerIds();
            ids.Add(_id);
            _service.SetActiveServers(ids);
        }

        public void Unregister()
        {
            if (_service == null) return;
            try
            {
                IList<Guid> ids = _service.GetActiveServerIds();
                ids.Remove(_id);
                _service.SetActiveServers(ids);
                _service.RemoveServer(_id);
            }
            catch { }
            _service = null;
        }

        private void Build()
        {
            var segs = new List<XYZ[]>();
            var cols = new List<ColorWithTransparency>();
            var guideColor = new ColorWithTransparency(34, 184, 219, 0);

            // 0° guide (global X axis) - single line
            segs.Add(new[]
            {
                new XYZ(_origin.X - _half, _origin.Y, _origin.Z),
                new XYZ(_origin.X + _half, _origin.Y, _origin.Z)
            });
            cols.Add(guideColor);

            // 90° guide (global Y axis) - single line
            segs.Add(new[]
            {
                new XYZ(_origin.X, _origin.Y - _half, _origin.Z),
                new XYZ(_origin.X, _origin.Y + _half, _origin.Z)
            });
            cols.Add(guideColor);

            _lineCount = segs.Count;
            int vCount = _lineCount * 2;

            int vSize = vCount * VertexPositionColored.GetSizeInFloats();
            _vf = new VertexFormat(VertexFormatBits.PositionColored);
            _vb = new VertexBuffer(vSize);
            _vb.Map(vSize);
            VertexStreamPositionColored vs = _vb.GetVertexStreamPositionColored();
            for (int i = 0; i < _lineCount; i++)
            {
                vs.AddVertex(new VertexPositionColored(segs[i][0], cols[i]));
                vs.AddVertex(new VertexPositionColored(segs[i][1], cols[i]));
            }
            _vb.Unmap();

            int iSize = _lineCount * IndexLine.GetSizeInShortInts();
            _ib = new IndexBuffer(iSize);
            _ib.Map(iSize);
            IndexStreamLine ist = _ib.GetIndexStreamLine();
            for (int i = 0; i < _lineCount; i++)
                ist.AddLine(new IndexLine(2 * i, 2 * i + 1));
            _ib.Unmap();

            _ei = new EffectInstance(VertexFormatBits.PositionColored);
            _built = true;
        }

        // ---- IDirectContext3DServer ----
        public void RenderScene(Autodesk.Revit.DB.View dBView, DisplayStyle displayStyle)
        {
            try
            {
                if (!_built) Build();
                DrawContext.FlushBuffer(_vb, _lineCount * 2, _ib, _lineCount * 2, _vf, _ei,
                    PrimitiveType.LineList, 0, _lineCount);
            }
            catch { }
        }

        public bool CanExecute(Autodesk.Revit.DB.View dBView) { return true; }
        public bool UseInTransparentPass(Autodesk.Revit.DB.View dBView) { return false; }

        public Outline GetBoundingBox(Autodesk.Revit.DB.View dBView)
        {
            return new Outline(
                new XYZ(_origin.X - _half, _origin.Y - _half, _origin.Z - 1),
                new XYZ(_origin.X + _half, _origin.Y + _half, _origin.Z + 1));
        }

        public Guid GetServerId() { return _id; }
        public ExternalServiceId GetServiceId() { return ExternalServices.BuiltInExternalServices.DirectContext3DService; }
        public string GetName() { return "PipeAligner guide cross"; }
        public string GetVendorId() { return "PipeAligner"; }
        public string GetDescription() { return "Temporary ghost cross used to choose the alignment guide."; }
        public string GetApplicationId() { return ""; }
        public string GetSourceId() { return ""; }
        public bool UsesHandles() { return false; }
    }

    // ------------------------------------------------------------------------------
    //  API Bridge (ExternalEvent)
    // ------------------------------------------------------------------------------
    internal sealed class AlignExternalHandler : IExternalEventHandler
    {
        public AlignRequest Request;
        public AlignSession Session;
        public AlignWindow Window;

        public void Execute(UIApplication app)
        {
            AlignRequest req = Request;
            UIDocument uidoc = null;
            try
            {
                uidoc = app.ActiveUIDocument;
                if (uidoc != null && !uidoc.Document.Equals(Session.Document)) uidoc = null;
            }
            catch { uidoc = null; }

            bool closing = false;
            try
            {
                switch (req)
                {
                    case AlignRequest.Cleanup:
                        closing = true;
                        AlignEngine.ClearVisuals(Session, uidoc);
                        break;

                    case AlignRequest.Retry:
                        closing = RunEvaluation(uidoc);
                        break;

                    case AlignRequest.DisconnectAndAlign:
                        if (Session.Conflict != null)
                        {
                            if (Session.Conflict.Kind == ConflictKind.Frontier)
                            {
                                Session.Policies[Session.Conflict.ConflictElementId] = FrontierPolicy.Disconnect;
                            }
                            else
                            {
                                // Detach the conflicting element; if it is not a separate element
                                // (e.g. Revit rejected the move), detach every connected family instead.
                                if (AlignEngine.DisconnectConflictElement(Session) == 0)
                                    Session.DisconnectAll = true;
                            }
                        }
                        closing = RunEvaluation(uidoc);
                        break;

                    case AlignRequest.MoveWithFamily:
                        if (Session.Conflict != null && Session.Conflict.Kind != ConflictKind.Frontier)
                        {
                            Window.ShowConflict(Session);
                            Window.AppendNote(
                                "'Move with Element' only applies when the chain touches a connected boundary family.");
                            break;
                        }
                        if (Session.Conflict != null && Session.Conflict.Kind == ConflictKind.Frontier)
                        {
                            if (Session.Conflict.RiskCount > 0)
                            {
                                var td = new TaskDialog("Move with Element");
                                td.MainInstruction = "Domino effect detected";
                                td.MainContent =
                                    "Moving the family would compromise " + Session.Conflict.RiskCount +
                                    " branch(es) connected to other fixed elements:\n" +
                                    Session.Conflict.RiskText +
                                    "\n\nThose branches will be DISCONNECTED before moving. Continue?";
                                td.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                                if (td.Show() != TaskDialogResult.Yes) break;
                            }
                            Session.Policies[Session.Conflict.ConflictElementId] = FrontierPolicy.MoveWith;
                        }
                        closing = RunEvaluation(uidoc);
                        break;

                    case AlignRequest.MoveChain:
                        // Moves the whole chain (segment, fittings and adjacent runs) rigidly.
                        // Boundary families are detached automatically, so the flow never blocks.
                        Session.MoveChainMode = true;
                        closing = RunEvaluation(uidoc);
                        break;
                }

                if (closing && req != AlignRequest.Cleanup)
                    AfterResolved(uidoc);
            }
            catch (Exception ex)
            {
                Session.MoveChainMode = false;
                if (req == AlignRequest.Cleanup) closing = true;
                else Window.SetStatus("Error", ex.Message);
            }
            finally
            {
                if (closing) Window.ForceClose();
                else Window.SetBusy(false);
            }
        }

        /// <summary>Clears the highlight, re-evaluates and returns true if everything got aligned (closes the window).</summary>
        private bool RunEvaluation(UIDocument uidoc)
        {
            AlignEngine.ClearVisuals(Session, uidoc);
            bool done = AlignEngine.ProcessPending(Session, uidoc);
            if (done)
                return true;
            Window.ShowConflict(Session);
            return false;
        }

        /// <summary>Everything resolved: refreshes the floating connector palette and reports skipped segments.</summary>
        private void AfterResolved(UIDocument uidoc)
        {
            if (uidoc != null && Session.TargetIds.Count > 0)
            {
                try
                {
                    AlignElementsSuite.ConnectorPalette.ShowFor(uidoc, Session.PivotId,
                        Session.PivotConnectorId, Session.TargetIds[0]);
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(Session.Note))
            {
                string note = Session.Note;
                Session.Note = null;
                TaskDialog.Show("Align to Connector", note);
            }
        }

        public string GetName() { return "PipeAligner.ExternalHandler"; }
    }

    // ------------------------------------------------------------------------------
    //  Conflict Window (Dark Slate, Code-Only WPF, Text-Only Buttons, Always Enabled)
    // ------------------------------------------------------------------------------
    internal sealed class AlignWindow : Window
    {
        private readonly TextBlock _title = new TextBlock();
        private readonly TextBlock _detail = new TextBlock();
        private readonly Button _retry = new Button();
        private readonly Button _disc = new Button();
        private readonly Button _move = new Button();
        private readonly Button _chain = new Button();
        private readonly Button _close = new Button();
        private bool _forceClose;
        private bool _cleanupRequested;
        private bool _busy;

        public Action<AlignRequest> RequestAction;

        private const string DarkButtonStyleXaml =
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "      xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
            "  <Setter Property='Background' Value='#4A5668'/>" +
            "  <Setter Property='Foreground' Value='#F2F5F9'/>" +
            "  <Setter Property='BorderBrush' Value='#5F6C80'/>" +
            "  <Setter Property='BorderThickness' Value='1'/>" +
            "  <Setter Property='Padding' Value='8,5,8,5'/>" +
            "  <Setter Property='Cursor' Value='Hand'/>" +
            "  <Setter Property='Template'>" +
            "    <Setter.Value>" +
            "      <ControlTemplate TargetType='Button'>" +
            "        <Border x:Name='bd' Background='{TemplateBinding Background}' " +
            "                BorderBrush='{TemplateBinding BorderBrush}' " +
            "                BorderThickness='{TemplateBinding BorderThickness}' " +
            "                CornerRadius='3' Padding='{TemplateBinding Padding}'>" +
            "          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
            "        </Border>" +
            "        <ControlTemplate.Triggers>" +
            "          <Trigger Property='IsMouseOver' Value='True'>" +
            "            <Setter TargetName='bd' Property='Background' Value='#5B6B81'/>" +
            "          </Trigger>" +
            "          <Trigger Property='IsPressed' Value='True'>" +
            "            <Setter TargetName='bd' Property='Background' Value='#2F3846'/>" +
            "          </Trigger>" +
            "        </ControlTemplate.Triggers>" +
            "      </ControlTemplate>" +
            "    </Setter.Value>" +
            "  </Setter>" +
            "</Style>";

        public AlignWindow(AlignSession session)
        {
            Title = "Align to Connector";
            Width = 460;
            MaxHeight = 760;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;
            ShowInTaskbar = false;
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 68, 83));
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 80;
            Top = 120;

            System.Windows.Style st =
                (System.Windows.Style)System.Windows.Markup.XamlReader.Parse(DarkButtonStyleXaml);
            Resources.Add(typeof(Button), st);

            var light = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 245, 249));
            var soft = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(205, 213, 224));

            // ---- Header (darker, draggable) ----
            var headerTitle = new TextBlock
            {
                Text = "Align to Connector",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = soft,
                VerticalAlignment = VerticalAlignment.Center
            };
            var headerClose = new Button
            {
                Content = "×",
                Width = 24,
                Height = 20,
                Padding = new Thickness(0),
                FontSize = 14,
                ToolTip = "Close",
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };
            headerClose.Click += (s, e) => Close();

            var headerRow = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(headerClose, Dock.Right);
            headerRow.Children.Add(headerClose);
            headerRow.Children.Add(headerTitle);

            var header = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 56, 69)),
                Padding = new Thickness(10, 4, 6, 4),
                Child = headerRow
            };
            header.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };

            // ---- Body ----
            _title.FontWeight = FontWeights.Bold;
            _title.FontSize = 14;
            _title.Foreground = light;
            _title.TextWrapping = TextWrapping.Wrap;
            _title.Margin = new Thickness(0, 0, 0, 8);

            _detail.TextWrapping = TextWrapping.Wrap;
            _detail.Foreground = soft;

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 300,
                Margin = new Thickness(0, 0, 0, 12),
                Content = _detail
            };

            _retry.Content = "Recalculate / Re-evaluate";
            _disc.Content = "Disconnect and Align";
            _move.Content = "Move with Element";
            _chain.Content = "Move Chain Elements";
            _close.Content = "Cancel / Close";
            foreach (Button b in new[] { _retry, _disc, _move, _chain, _close })
                b.Margin = new Thickness(0, 0, 0, 6);

            _retry.Click += (s, e) => Fire(AlignRequest.Retry);
            _disc.Click += (s, e) => Fire(AlignRequest.DisconnectAndAlign);
            _move.Click += (s, e) => Fire(AlignRequest.MoveWithFamily);
            _chain.Click += (s, e) => Fire(AlignRequest.MoveChain);
            _close.Click += (s, e) => Close();

            var body = new StackPanel { Margin = new Thickness(12) };
            body.Children.Add(_title);
            body.Children.Add(scroll);
            body.Children.Add(_retry);
            body.Children.Add(_disc);
            body.Children.Add(_move);
            body.Children.Add(_chain);
            body.Children.Add(_close);

            var root = new StackPanel();
            root.Children.Add(header);
            root.Children.Add(body);

            Content = new Border
            {
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(90, 102, 122)),
                BorderThickness = new Thickness(1),
                Child = root
            };

            Closing += OnClosing;
        }

        private void Fire(AlignRequest req)
        {
            // Buttons are never disabled; while a request is running extra clicks are simply ignored.
            if (_busy && req != AlignRequest.Cleanup) return;
            _busy = true;
            if (RequestAction != null) RequestAction(req);
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_forceClose) return;

            // Does not close until the cleanup transaction has finished
            e.Cancel = true;
            if (_cleanupRequested) return;
            _cleanupRequested = true;
            Fire(AlignRequest.Cleanup);
        }

        public void ForceClose()
        {
            _forceClose = true;
            try { Close(); } catch { }
        }

        /// <summary>Only toggles an internal "request running" flag; the buttons stay enabled.</summary>
        public void SetBusy(bool busy)
        {
            _busy = busy;
        }

        public void SetStatus(string title, string detail)
        {
            _title.Text = title;
            _detail.Text = detail;
        }

        public void AppendNote(string note)
        {
            _detail.Text = _detail.Text + "\n\n" + note;
        }

        public void ShowConflict(AlignSession s)
        {
            ConflictInfo c = s.Conflict;
            if (c == null)
            {
                SetStatus("No conflicts", "There are no pending clashes.");
                return;
            }

            Document doc = s.Document;
            double f = 304.8;
            string title = "Conflict on segment " + (s.NextIndex + 1) + " of " + s.TargetIds.Count;
            string detail =
                "Type: " + AlignEngine.KindText(c.Kind) + "\n" +
                "Segment (orange): " + AlignEngine.Describe(doc, c.CurveId) + "\n" +
                "Conflicting element (red): " + AlignEngine.Describe(doc, c.ConflictElementId) + "\n" +
                "Point (mm): X=" + (c.Point.X * f).ToString("0") +
                "  Y=" + (c.Point.Y * f).ToString("0") +
                "  Z=" + (c.Point.Z * f).ToString("0") + "\n\n" +
                c.Message;

            if (c.Kind == ConflictKind.Frontier)
            {
                detail += c.RiskCount > 0
                    ? "\n\nDomino effect: moving the family would compromise " + c.RiskCount +
                      " branch(es) towards fixed elements:\n" + c.RiskText
                    : "\n\nDomino effect: moving the family would only stretch free runs.";
            }
            else
            {
                detail += "\n\nResolve the conflict in Revit and press Recalculate, use 'Disconnect and Align' " +
                          "to detach the conflicting element, or 'Move Chain Elements' to move the whole chain.";
            }

            SetStatus(title, detail);
        }
    }

    // ------------------------------------------------------------------------------
    //  Engine
    // ------------------------------------------------------------------------------
    internal static class AlignEngine
    {
        // ------------------------- Type Detection -------------------------

        public static bool IsPipe(Element e) { return e is Pipe; }

        public static bool IsConduit(Element e) { return e is Conduit; }

        /// <summary>An adjacent run only absorbs displacement if it has the same MEP curve type.</summary>
        private static bool SameKind(MEPCurve a, Element b)
        {
            return (IsPipe(a) && IsPipe(b)) || (IsConduit(a) && IsConduit(b));
        }

        private static double GetPipeRadius(Element e)
        {
            double d = 0;
            Parameter po = e.get_Parameter(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER);
            Parameter pn = e.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (po != null && po.HasValue) d = Math.Max(d, po.AsDouble());
            if (pn != null && pn.HasValue) d = Math.Max(d, pn.AsDouble());
            return d / 2.0;
        }

        private static double GetConduitRadius(Element e)
        {
            double d = 0;
            Parameter po = e.get_Parameter(BuiltInParameter.RBS_CONDUIT_OUTER_DIAM_PARAM);
            Parameter pn = e.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
            if (po != null && po.HasValue) d = Math.Max(d, po.AsDouble());
            if (pn != null && pn.HasValue) d = Math.Max(d, pn.AsDouble());
            return d / 2.0;
        }

        private static double GetRadius(MEPCurve c)
        {
            double r = 0;
            if (IsPipe(c)) r = GetPipeRadius(c);
            else if (IsConduit(c)) r = GetConduitRadius(c);
            return r < 0.01 ? 0.01 : r;
        }

        // ------------------------- Main Flow -------------------------

        /// <summary>
        /// Processes pending segments in order. Stops at the first conflict
        /// (highlights it and returns false). Returns true if all are aligned.
        /// </summary>
        public static bool ProcessPending(AlignSession s, UIDocument uidoc)
        {
            try { return ProcessPendingCore(s, uidoc); }
            finally { s.MoveChainMode = false; }
        }

        private static bool ProcessPendingCore(AlignSession s, UIDocument uidoc)
        {
            Document doc = s.Document;
            s.Conflict = null;

            while (s.NextIndex < s.TargetIds.Count)
            {
                MEPCurve p = doc.GetElement(s.TargetIds[s.NextIndex]) as MEPCurve;
                if (p == null) { s.NextIndex++; continue; }

                ChainResult r = Evaluate(doc, s, p);

                if (r.AlreadyAligned)
                {
                    // Nothing moved: explain why instead of exiting silently
                    XYZ cpt = PivotCenter(doc, s);
                    XYZ mpt = MidPoint(p);
                    double dz = (cpt != null && mpt != null) ? Math.Abs(cpt.Z - mpt.Z) : 0;
                    s.Note = "Segment [Id " + p.Id + "] was not moved: in plan (X, Y) it is already aligned with the connector " +
                             "(difference " + (r.Displacement.GetLength() / Cfg.MmToFt).ToString("0.##") + " mm).\n" +
                             "This command only aligns in plan and does not change the Z elevation" +
                             (dz > Cfg.AlignTolFt ? " (Z difference: " + (dz / Cfg.MmToFt).ToString("0") + " mm)." : ".");
                    s.MoveChainMode = false;
                    s.NextIndex++;
                    continue;
                }

                if (r.Conflict == null)
                {
                    r.Conflict = ApplyAlignment(doc, s, r);

                    // If Revit rejects the adjustment (e.g. an elbow cannot keep connection while stretching neighbour),
                    // retry moving neighbour conduits as a whole without stretching any.
                    if (r.Conflict != null && r.Conflict.Kind == ConflictKind.RevitRejected)
                    {
                        s.ForceRigid = true;
                        try
                        {
                            ChainResult r2 = Evaluate(doc, s, p);
                            if (!r2.AlreadyAligned)
                            {
                                if (r2.Conflict == null) r2.Conflict = ApplyAlignment(doc, s, r2);
                                r = r2;
                            }
                        }
                        finally { s.ForceRigid = false; }
                    }
                }

                if (r.Conflict != null)
                {
                    s.Conflict = r.Conflict;
                    HighlightConflict(s, uidoc);
                    return false;
                }

                // Segment done: "Move Chain Elements" only applied to it, the next pending segment is evaluated normally
                s.MoveChainMode = false;
                s.NextIndex++;
            }
            return true;
        }

        // ------------------------- Displacement (by Selected Guide) -------------------------

        public static XYZ MidPoint(Element e)
        {
            if (e == null) return null;
            LocationCurve lc = e.Location as LocationCurve;
            if (lc != null) return lc.Curve.Evaluate(0.5, true);
            LocationPoint lp = e.Location as LocationPoint;
            if (lp != null) return lp.Point;
            BoundingBoxXYZ bb = e.get_BoundingBox(null);
            return bb != null ? bb.Min.Add(bb.Max).Multiply(0.5) : null;
        }

        public static XYZ PivotCenter(Document doc, AlignSession s)
        {
            if (s.PivotConnectorId >= 0)
            {
                Element pe = doc.GetElement(s.PivotId);
                if (pe != null)
                {
                    foreach (Connector c in GetConnectors(pe))
                        if (c.Id == s.PivotConnectorId) return c.Origin;
                }
                if (s.PivotPointOverride != null) return s.PivotPointOverride;
            }
            return MidPoint(doc.GetElement(s.PivotId));
        }

        /// <summary>
        /// EqualizeY: keeps X and Z, equalizes Y to pivot. EqualizeX: keeps Y and Z, equalizes X.
        /// If the segment is parallel to the alignment direction it cannot be aligned along that axis.
        /// </summary>
        private static void AddUnique(List<XYZ> list, XYZ v)
        {
            foreach (XYZ k in list)
                if (k.DistanceTo(v) < 1e-6) return;
            list.Add(v);
        }

        private static bool NeighboursAccept(Document doc, Dictionary<ElementId, List<XYZ>> neighbours, XYZ cand)
        {
            foreach (var kv in neighbours)
            {
                if (kv.Value.Count > 1) continue;
                MEPCurve q = doc.GetElement(kv.Key) as MEPCurve;
                if (q == null) continue;
                XYZ near;
                double amount;
                if (CheckNeighbour(q, kv.Value[0], cand, out near, out amount) != NbResult.Ok) return false;
            }
            return true;
        }

        private static List<XYZ> ComputeCandidates(Document doc, AlignSession s, Line axis, out string error)
        {
            var list = new List<XYZ>();
            error = null;

            XYZ c = PivotCenter(doc, s);
            if (c == null) { error = "The reference connector could not be read."; return list; }

            XYZ mid = axis.Evaluate(0.5, true);
            if (s.Axis != AlignAxis.Auto)
            {
                XYZ dir = s.Axis == AlignAxis.EqualizeY ? XYZ.BasisY : XYZ.BasisX;
                if (Math.Abs(axis.Direction.DotProduct(dir)) > 0.999)
                {
                    error = "The segment is parallel to the alignment direction.";
                    return list;
                }
                double deltaFixed = s.Axis == AlignAxis.EqualizeY ? c.Y - mid.Y : c.X - mid.X;
                list.Add(dir.Multiply(deltaFixed));
                return list;
            }

            XYZ v = new XYZ(c.X - mid.X, c.Y - mid.Y, 0);
            XYZ uh = new XYZ(axis.Direction.X, axis.Direction.Y, 0);
            if (uh.GetLength() < 1e-3)
            {
                list.Add(v);
                return list;
            }

            uh = uh.Normalize();
            XYZ n = new XYZ(-uh.Y, uh.X, 0);
            double delta = v.DotProduct(n);
            AddUnique(list, n.Multiply(delta));
            if (Math.Abs(n.X) > 0.2) AddUnique(list, new XYZ(delta / n.X, 0, 0));
            if (Math.Abs(n.Y) > 0.2) AddUnique(list, new XYZ(0, delta / n.Y, 0));
            return list;
        }

        private static XYZ ComputeDisplacement(Document doc, AlignSession s, Line axis, out string error)
        {
            List<XYZ> list = ComputeCandidates(doc, s, axis, out error);
            return list.Count > 0 ? list[0] : XYZ.Zero;
        }

        // ------------------------- Chain Evaluation (3 Passes) -------------------------

        private enum NbResult { Ok, NoGeometry, Skew, NoSlack }

        private static ChainResult Evaluate(Document doc, AlignSession s, MEPCurve p)
        {
            var r = new ChainResult { Curve = p };
            r.ChainIds.Add(p.Id);

            Line axis = GetLine(p);
            if (axis == null)
            {
                r.Fail(ConflictKind.InvalidGeometry, CenterOf(p), p.Id,
                    "The segment does not have an evaluable linear geometry.");
                return r;
            }

            string err;
            List<XYZ> candidates = ComputeCandidates(doc, s, axis, out err);
            if (err != null || candidates.Count == 0)
            {
                r.Fail(ConflictKind.InvalidGeometry, CenterOf(p), p.Id, err ?? "The displacement could not be calculated.");
                return r;
            }
            XYZ d = candidates[0];
            r.Displacement = d;
            if (d.GetLength() < Cfg.AlignTolFt) { r.AlreadyAligned = true; return r; }

            // ===== Pass 1: Main chain through connectors =====
            var movers = new HashSet<ElementId> { p.Id };
            var queue = new Queue<Element>();
            queue.Enqueue(p);

            var neighbours = new Dictionary<ElementId, List<XYZ>>();

            while (queue.Count > 0)
            {
                Element cur = queue.Dequeue();
                foreach (Connector c in GetConnectors(cur))
                {
                    foreach (Connector other in GetRefs(c, cur))
                    {
                        Element o = other.Owner;
                        if (o == null || movers.Contains(o.Id)) continue;

                        // Pivot is the reference: never moves and is never a boundary.
                        // Only record the link so it is disconnected on apply.
                        if (o.Id.Equals(s.PivotId))
                        {
                            r.PivotLinks.Add(new KeyValuePair<Connector, Connector>(c, other));
                            continue;
                        }

                        if (IsFitting(o))
                        {
                            movers.Add(o.Id);
                            r.ChainIds.Add(o.Id);
                            queue.Enqueue(o);
                        }
                        else if (o is MEPCurve && SameKind(p, o))
                        {
                            // If the neighbour cannot absorb the movement (skewed / no slack),
                            // it is NOT a conflict: the curve simply moves rigidly with the chain.
                            XYZ nbNear;
                            double nbAmt;
                            if (s.ForceRigid || s.MoveChainMode ||
                                CheckNeighbour((MEPCurve)o, other.Origin, d, out nbNear, out nbAmt) != NbResult.Ok)
                            {
                                movers.Add(o.Id);
                                r.ChainIds.Add(o.Id);
                                neighbours.Remove(o.Id);
                                r.Joints.RemoveAll(j => j.NeighbourId.Equals(o.Id));
                                queue.Enqueue(o);
                                continue;
                            }

                            List<XYZ> list;
                            if (!neighbours.TryGetValue(o.Id, out list))
                            {
                                list = new List<XYZ>();
                                neighbours[o.Id] = list;
                            }
                            list.Add(other.Origin);
                            r.Joints.Add(new ChainJoint
                            {
                                MoverId = cur.Id,
                                MoverConnId = c.Id,
                                NeighbourId = o.Id,
                                NeighbourConnId = other.Id,
                                Origin = other.Origin
                            });
                        }
                        else
                        {
                            // Boundary family (device, equipment, box...). Every connector pair is kept.
                            FrontierInfo known = null;
                            foreach (FrontierInfo f in r.Frontiers)
                                if (f.Family.Id.Equals(o.Id)) { known = f; break; }
                            if (known == null)
                            {
                                known = new FrontierInfo { Family = o, ChainSide = c, FamilySide = other };
                                r.Frontiers.Add(known);
                                r.FrontierIds.Add(o.Id);
                            }
                            known.Pairs.Add(new[] { c, other });
                        }
                    }
                }
            }
            foreach (ElementId mid in movers) r.MoverIds.Add(mid);

            r.Displacement = d;

            // ===== Pass 2: Boundary families + domino effect =====
            foreach (FrontierInfo fi in r.Frontiers)
            {
                AnalyzeDomino(doc, p, d, fi, r.ChainIds);

                // The user already decided, or the chain is being moved rigidly (families are detached on apply)
                if (s.MoveChainMode || s.DisconnectAll || s.Policies.ContainsKey(fi.Family.Id)) continue;

                ConflictInfo cf = r.Fail(ConflictKind.Frontier, fi.FamilySide.Origin, fi.Family.Id,
                    "The chain touches a boundary family. Choose 'Disconnect and Align' (aligns only the segment), " +
                    "'Move with Element' (the family moves together with the chain) or 'Move Chain Elements' (moves the whole chain and detaches it from the family).");
                cf.RiskCount = fi.Risks.Count;
                cf.RiskText = string.Join("\n", fi.Risks.Select(k =>
                    "• " + Describe(doc, k.Blocker == null ? ElementId.InvalidElementId : k.Blocker.Id) +
                    " — " + k.Reason));
                return r;
            }

            // ===== Fuses: Adjacent straight runs =====
            foreach (var kv in neighbours)
            {
                MEPCurve q = doc.GetElement(kv.Key) as MEPCurve;
                if (q == null) continue;
                r.ChainIds.Add(q.Id);

                if (kv.Value.Count > 1) continue;   // Both ends move: only translated

                XYZ near;
                double amount;
                NbResult nb = CheckNeighbour(q, kv.Value[0], d, out near, out amount);
                if (nb == NbResult.Ok) continue;

                if (nb == NbResult.NoGeometry)
                    r.Fail(ConflictKind.InvalidGeometry, near, q.Id, "An adjacent run does not have an evaluable linear geometry.");
                else if (nb == NbResult.Skew)
                    r.Fail(ConflictKind.Skew, near, q.Id,
                        "An adjacent run is not parallel to the movement: it would end up skewed (" +
                        (amount / Cfg.MmToFt).ToString("0.#") + " mm of lateral deviation).");
                else
                    r.Fail(ConflictKind.NoSlack, near, q.Id,
                        "An adjacent run cannot absorb the movement: it is missing " +
                        (amount / Cfg.MmToFt).ToString("0.#") + " mm of length.");
                return r;
            }

            // ===== Pass 3: Physical collision =====
            // "Move Chain Elements" is an explicit user override: the collision test is skipped
            if (!s.MoveChainMode)
            {
                ConflictInfo col = CheckCollisions(doc, s, p, axis, d, r);
                if (col != null) r.Conflict = col;
            }

            return r;
        }

        /// <summary>Adjacent run keeps its far end fixed; the near end follows the displacement.</summary>
        private static NbResult CheckNeighbour(MEPCurve q, XYZ junction, XYZ d, out XYZ near, out double amountFt)
        {
            near = junction;
            amountFt = 0;

            Line ql = GetLine(q);
            if (ql == null) return NbResult.NoGeometry;

            XYZ e0 = ql.GetEndPoint(0), e1 = ql.GetEndPoint(1);
            near = e0.DistanceTo(junction) <= e1.DistanceTo(junction) ? e0 : e1;
            XYZ far = near.IsAlmostEqualTo(e0) ? e1 : e0;

            XYZ v = near.Subtract(far);
            if (v.GetLength() < 1e-9) return NbResult.NoGeometry;
            XYZ v2 = near.Add(d).Subtract(far);
            XYZ u = v.Normalize();

            XYZ skew = d.Subtract(u.Multiply(d.DotProduct(u)));
            if (skew.GetLength() > Cfg.SkewTolFt)
            {
                amountFt = skew.GetLength();
                return NbResult.Skew;
            }

            double minLen = Cfg.MinNeighbourLengthMm * Cfg.MmToFt;
            double len2 = v2.GetLength();
            bool flipped = v2.DotProduct(v) <= 0;
            if (flipped || len2 < minLen)
            {
                amountFt = flipped ? len2 + minLen : minLen - len2;
                return NbResult.NoSlack;
            }
            return NbResult.Ok;
        }

        /// <summary>
        /// Domino effect: exits through OTHER connectors of the boundary family and checks whether moving it
        /// would drag a third fixed element or only stretch free runs.
        /// </summary>
        private static void AnalyzeDomino(Document doc, MEPCurve p, XYZ d, FrontierInfo fi, HashSet<ElementId> chainIds)
        {
            var visited = new HashSet<ElementId>(chainIds) { fi.Family.Id };

            foreach (Connector fc in GetConnectors(fi.Family))
            {
                bool isJoint = false;
                foreach (Connector[] pr in fi.Pairs)
                    if (pr[1].Id == fc.Id) { isJoint = true; break; }
                if (isJoint) continue;   // Joints with our chain

                foreach (Connector first in GetRefs(fc, fi.Family))
                {
                    if (first.Owner == null || visited.Contains(first.Owner.Id)) continue;
                    DominoRisk risk = TraceBranch(p, d, fc, first, visited);
                    if (risk != null) fi.Risks.Add(risk);
                }
            }
        }

        private static DominoRisk TraceBranch(MEPCurve p, XYZ d, Connector fc, Connector first, HashSet<ElementId> visited)
        {
            var queue = new Queue<KeyValuePair<Element, Connector>>();
            queue.Enqueue(new KeyValuePair<Element, Connector>(first.Owner, first));
            var seen = new HashSet<ElementId>();

            while (queue.Count > 0)
            {
                KeyValuePair<Element, Connector> kv = queue.Dequeue();
                Element o = kv.Key;
                Connector entry = kv.Value;
                if (o == null || visited.Contains(o.Id) || !seen.Add(o.Id)) continue;

                if (IsFitting(o))
                {
                    // Elbows/tees: branch continues (they move with the family)
                    foreach (Connector c in GetConnectors(o))
                    {
                        if (c.Id == entry.Id) continue;
                        foreach (Connector other in GetRefs(c, o))
                            queue.Enqueue(new KeyValuePair<Element, Connector>(other.Owner, other));
                    }
                }
                else if (o is MEPCurve && SameKind(p, o))
                {
                    XYZ near;
                    double amount;
                    NbResult nb = CheckNeighbour((MEPCurve)o, entry.Origin, d, out near, out amount);
                    if (nb != NbResult.Ok)
                        return new DominoRisk
                        {
                            OnFamily = fc,
                            FirstOther = first,
                            Blocker = o,
                            Point = near,
                            Reason = nb == NbResult.Skew
                                ? "the run cannot absorb the movement without becoming skewed"
                                : "the run is not long enough to absorb the movement"
                        };
                }
                else
                {
                    // Another connected fixed family: moving this family would drag it
                    return new DominoRisk
                    {
                        OnFamily = fc,
                        FirstOther = first,
                        Blocker = o,
                        Point = entry.Origin,
                        Reason = "connected family that would be dragged along"
                    };
                }
            }
            return null;
        }

        // ------------------------- Collision (Sweep) -------------------------

        private static ConflictInfo CheckCollisions(Document doc, AlignSession s, MEPCurve p, Line axis, XYZ d, ChainResult r)
        {
            double radius = GetRadius(p);
            XYZ u = axis.Direction;

            double along = d.DotProduct(u);                         // Displacement component along the axis
            XYZ dPerp = d.Subtract(u.Multiply(along));              // Lateral component
            double dl = dPerp.GetLength();
            if (dl < 1e-6) return null;                             // Only slides along its own axis

            XYZ dirD = dPerp.Normalize();
            XYZ w = u.CrossProduct(dirD);
            if (w.GetLength() < 1e-9) return null;
            w = w.Normalize();

            double shrink = Cfg.SweepEndShrinkMm * Cfg.MmToFt;
            double length = axis.Length + Math.Abs(along) - 2 * shrink;
            if (length <= 0) return null;
            XYZ start = axis.GetEndPoint(0).Add(u.Multiply(shrink + Math.Min(0, along)));

            XYZ a = start.Subtract(dirD.Multiply(radius)).Subtract(w.Multiply(radius));
            XYZ b = start.Add(dirD.Multiply(dl + radius)).Subtract(w.Multiply(radius));
            XYZ c = start.Add(dirD.Multiply(dl + radius)).Add(w.Multiply(radius));
            XYZ e = start.Subtract(dirD.Multiply(radius)).Add(w.Multiply(radius));

            Solid sweep;
            try
            {
                var loop = new CurveLoop();
                loop.Append(Line.CreateBound(a, b));
                loop.Append(Line.CreateBound(b, c));
                loop.Append(Line.CreateBound(c, e));
                loop.Append(Line.CreateBound(e, a));
                sweep = GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { loop }, u, length);
            }
            catch
            {
                return null;
            }

            XYZ[] corners = { a, b, c, e };
            XYZ shift = u.Multiply(length);
            var all = new List<XYZ>(corners);
            foreach (XYZ k in corners) all.Add(k.Add(shift));
            double pad = Cfg.MmToFt;
            XYZ sMin = new XYZ(all.Min(q => q.X) - pad, all.Min(q => q.Y) - pad, all.Min(q => q.Z) - pad);
            XYZ sMax = new XYZ(all.Max(q => q.X) + pad, all.Max(q => q.Y) + pad, all.Max(q => q.Z) + pad);

            // Exclude the chain, frontier families (already evaluated), and other targets (also moving)
            var exclude = new HashSet<ElementId>(r.ChainIds);
            foreach (ElementId id in r.FrontierIds) exclude.Add(id);
            foreach (ElementId id in s.TargetIds) exclude.Add(id);
            exclude.Add(p.Id);
            exclude.Add(s.PivotId);   // Reference family does not count as an obstacle

            ICollection<ElementId> hits;
            try
            {
                hits = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory>(Cfg.CollisionCategories)))
                    .WherePasses(new BoundingBoxIntersectsFilter(new Outline(sMin, sMax)))
                    .WherePasses(new ElementIntersectsSolidFilter(sweep))
                    .ToElementIds();
            }
            catch
            {
                return null;
            }

            Element bestEl = null;
            XYZ bestPt = null;
            double bestDist = double.MaxValue;
            foreach (ElementId id in hits)
            {
                if (exclude.Contains(id)) continue;
                Element el = doc.GetElement(id);
                if (el == null) continue;
                if (el is MEPCurve || IsFitting(el)) continue;   // Conduits/pipes/fittings do not block
                XYZ pt = IntersectionCenter(el, sMin, sMax);
                double dist = pt.DistanceTo(start);
                if (dist < bestDist) { bestDist = dist; bestPt = pt; bestEl = el; }
            }

            if (bestEl == null) return null;

            return r.Fail(ConflictKind.Collision, bestPt, bestEl.Id,
                "The segment's displacement intersects another element of the model.");
        }

        private static XYZ IntersectionCenter(Element el, XYZ sMin, XYZ sMax)
        {
            BoundingBoxXYZ b = el.get_BoundingBox(null);
            if (b == null) return sMin.Add(sMax).Multiply(0.5);

            XYZ mn = new XYZ(Math.Max(b.Min.X, sMin.X), Math.Max(b.Min.Y, sMin.Y), Math.Max(b.Min.Z, sMin.Z));
            XYZ mx = new XYZ(Math.Min(b.Max.X, sMax.X), Math.Min(b.Max.Y, sMax.Y), Math.Min(b.Max.Z, sMax.Z));
            if (mn.X > mx.X || mn.Y > mx.Y || mn.Z > mx.Z)
                return b.Min.Add(b.Max).Multiply(0.5);
            return mn.Add(mx).Multiply(0.5);
        }

        // ------------------------- Apply Alignment (Single Transaction) -------------------------

        private static ConflictInfo ApplyAlignment(Document doc, AlignSession s, ChainResult r)
        {
            MEPCurve p = r.Curve;
            var pre = new RollbackOnErrorPreprocessor();

            using (var t = new Transaction(doc, "Align segment"))
            {
                t.Start();
                try
                {
                    FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                    fho.SetFailuresPreprocessor(pre);
                    fho.SetClearAfterRollback(true);
                    t.SetFailureHandlingOptions(fho);

                    // 0) Detach chain from pivot (pivot never moves)
                    foreach (var link in r.PivotLinks)
                    {
                        try { link.Key.DisconnectFrom(link.Value); } catch { }
                    }

                    // 1) User decisions on boundary families
                    bool movedFamily = false;
                    foreach (FrontierInfo fi in r.Frontiers)
                    {
                        FrontierPolicy pol;
                        if (!s.Policies.TryGetValue(fi.Family.Id, out pol))
                        {
                            if (!s.MoveChainMode && !s.DisconnectAll) continue;
                            pol = FrontierPolicy.Disconnect;
                        }

                        if (pol == FrontierPolicy.Disconnect)
                        {
                            foreach (Connector[] pr in fi.Pairs)
                            {
                                try { pr[0].DisconnectFrom(pr[1]); }
                                catch { }
                            }
                        }
                        else
                        {
                            // Disconnect branches that would compromise another fixed element
                            foreach (DominoRisk risk in fi.Risks)
                                risk.OnFamily.DisconnectFrom(risk.FirstOther);

                            ElementTransformUtils.MoveElement(doc, fi.Family.Id, r.Displacement);
                            movedFamily = true;
                        }
                    }

                    // 2) Align segment (recalculate remaining delta if the family moved)
                    XYZ d = r.Displacement;
                    if (movedFamily)
                    {
                        doc.Regenerate();
                        Line now = GetLine(p);
                        string err = null;
                        d = now != null ? ComputeDisplacement(doc, s, now, out err) : XYZ.Zero;
                        if (err != null) d = XYZ.Zero;
                    }

                    if (d.GetLength() >= Cfg.AlignTolFt)
                        MoveChain(doc, r, d);

                    TransactionStatus st = t.Commit();
                    if (st == TransactionStatus.Committed) return null;

                    string detail = string.IsNullOrEmpty(pre.ErrorText) ? "." : ": " + pre.ErrorText;
                    if (pre.FailingIds.Count > 0)
                        detail += "\nElements involved: " +
                                  string.Join("; ", pre.FailingIds.Select(id => Describe(doc, id)));
                    return r.Fail(ConflictKind.RevitRejected, CenterOf(p), p.Id,
                        "Revit rejected the adjustment" + detail);
                }
                catch (Exception ex)
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    return r.Fail(ConflictKind.RevitRejected, CenterOf(p), p.Id,
                        "Revit rejected the adjustment: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Moves the segment together with its fittings (elbows, tees, unions) as a single body, and adjusts
        /// the length of adjacent straight runs: the near end follows the movement and the far end stays fixed.
        /// </summary>
        private static void MoveChain(Document doc, ChainResult r, XYZ d)
        {
            // Neighbours connected to the chain on two sides: do not stretch, only translate with it
            var joints = new Dictionary<ElementId, int>();
            foreach (ChainJoint j in r.Joints)
            {
                int n;
                joints.TryGetValue(j.NeighbourId, out n);
                joints[j.NeighbourId] = n + 1;
            }

            var moveIds = new List<ElementId>(r.MoverIds);
            var stretch = new List<KeyValuePair<MEPCurve, Line>>();
            var seen = new HashSet<ElementId>();

            foreach (ChainJoint j in r.Joints)
            {
                if (!seen.Add(j.NeighbourId)) continue;
                MEPCurve q = doc.GetElement(j.NeighbourId) as MEPCurve;
                if (q == null) continue;

                if (joints[j.NeighbourId] > 1) { moveIds.Add(q.Id); continue; }

                Line ql = GetLine(q);
                if (ql == null) continue;

                Connector nc = FindConnector(doc, j.NeighbourId, j.NeighbourConnId);
                XYZ junction = nc != null ? nc.Origin : j.Origin;
                XYZ e0 = ql.GetEndPoint(0), e1 = ql.GetEndPoint(1);
                bool nearIs0 = e0.DistanceTo(junction) <= e1.DistanceTo(junction);
                XYZ nearNew = (nearIs0 ? e0 : e1).Add(d);

                stretch.Add(new KeyValuePair<MEPCurve, Line>(q,
                    Line.CreateBound(nearIs0 ? nearNew : e0, nearIs0 ? e1 : nearNew)));
            }

            var stretched = new HashSet<ElementId>(stretch.Select(k => k.Key.Id));

            // 1) Disconnect joints on neighbours that will be stretched
            foreach (ChainJoint j in r.Joints)
            {
                if (!stretched.Contains(j.NeighbourId)) continue;
                Connector a = FindConnector(doc, j.MoverId, j.MoverConnId);
                Connector b = FindConnector(doc, j.NeighbourId, j.NeighbourConnId);
                if (a != null && b != null) a.DisconnectFrom(b);
            }

            // 2) Move segment + fittings together
            ElementTransformUtils.MoveElements(doc, moveIds, d);

            // 3) Lengthen / shorten neighbours
            foreach (var k in stretch)
                ((LocationCurve)k.Key.Location).Curve = k.Value;

            // 4) Reconnect
            foreach (ChainJoint j in r.Joints)
            {
                if (!stretched.Contains(j.NeighbourId)) continue;
                Connector a = FindConnector(doc, j.MoverId, j.MoverConnId);
                Connector b = FindConnector(doc, j.NeighbourId, j.NeighbourConnId);
                if (a != null && b != null) { try { a.ConnectTo(b); } catch { } }
            }
        }

        /// <summary>
        /// "Disconnect and Align" for non-boundary conflicts: detaches the conflicting element from the chain.
        /// Returns the number of connections removed.
        /// </summary>
        public static int DisconnectConflictElement(AlignSession s)
        {
            Document doc = s.Document;
            ConflictInfo c = s.Conflict;
            if (c == null || c.ConflictElementId == null || c.ConflictElementId.Equals(ElementId.InvalidElementId)) return 0;
            if (c.ConflictElementId.Equals(c.CurveId)) return 0;

            Element ce = doc.GetElement(c.ConflictElementId);
            if (ce == null) return 0;

            var pairs = new List<Connector[]>();
            foreach (Connector k in GetConnectors(ce))
                foreach (Connector o in GetRefs(k, ce))
                    if (o.Owner != null && c.ChainIds.Contains(o.Owner.Id))
                        pairs.Add(new[] { k, o });

            if (pairs.Count == 0) return 0;

            using (var t = new Transaction(doc, "Disconnect conflicting element"))
            {
                t.Start();
                try
                {
                    foreach (Connector[] pr in pairs) pr[0].DisconnectFrom(pr[1]);
                    t.Commit();
                }
                catch
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    return 0;
                }
            }
            return pairs.Count;
        }

        private static Connector FindConnector(Document doc, ElementId ownerId, int connId)
        {
            foreach (Connector c in GetConnectors(doc.GetElement(ownerId)))
                if (c.Id == connId) return c;
            return null;
        }

        // ------------------------- Highlight + Section Box -------------------------

        private static void HighlightConflict(AlignSession s, UIDocument uidoc)
        {
            Document doc = s.Document;
            ConflictInfo c = s.Conflict;
            View view = doc.ActiveView;
            s.ViewId = view.Id;

            ElementId solidFill = FindSolidFillId(doc);
            OverrideGraphicSettings red = BuildOverride(new Color(255, 0, 0), solidFill);
            OverrideGraphicSettings orange = BuildOverride(new Color(255, 140, 0), solidFill);

            using (var t = new Transaction(doc, "Highlight clash"))
            {
                t.Start();
                try
                {
                    var orangeIds = new List<ElementId>();
                    foreach (ElementId id in c.ChainIds)
                        if (!id.Equals(c.ConflictElementId)) orangeIds.Add(id);

                    // 0) Halftone for everything NOT connected to the conflict (kept until the clash is resolved)
                    HalftoneExcept(s, doc, view, BuildConflictKeepSet(s, doc, c));

                    var toSave = new List<ElementId>(orangeIds);
                    if (!c.ConflictElementId.Equals(ElementId.InvalidElementId)) toSave.Add(c.ConflictElementId);

                    // 1) Save previous graphic overrides
                    foreach (ElementId id in toSave)
                    {
                        if (doc.GetElement(id) == null) continue;
                        if (!s.SavedOverrides.ContainsKey(id))
                            s.SavedOverrides[id] = view.GetElementOverrides(id);
                    }

                    // 2) Orange: segment + chain. Red: frontier / obstacle
                    foreach (ElementId id in orangeIds)
                        if (doc.GetElement(id) != null) view.SetElementOverrides(id, orange);

                    if (!c.ConflictElementId.Equals(ElementId.InvalidElementId) &&
                        doc.GetElement(c.ConflictElementId) != null)
                        view.SetElementOverrides(c.ConflictElementId, red);

                    // 3) Section Box (3D views only)
                    ApplySectionBox(s, doc, view);

                    t.Commit();
                    s.VisualsActive = true;
                }
                catch
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    s.SavedOverrides.Clear();
                    s.SectionSaved = false;
                }
            }

            ZoomToConflict(s, uidoc);
        }

        /// <summary>Conflict element + active chain + pivot + everything directly connected to the conflict element.</summary>
        private static HashSet<ElementId> BuildConflictKeepSet(AlignSession s, Document doc, ConflictInfo c)
        {
            var keep = new HashSet<ElementId>(c.ChainIds);
            keep.Add(c.CurveId);
            keep.Add(s.PivotId);
            if (!c.ConflictElementId.Equals(ElementId.InvalidElementId))
            {
                keep.Add(c.ConflictElementId);
                Element ce = doc.GetElement(c.ConflictElementId);
                if (ce != null)
                    foreach (Connector k in GetConnectors(ce))
                        foreach (Connector o in GetRefs(k, ce))
                            if (o.Owner != null) keep.Add(o.Owner.Id);
            }
            return keep;
        }

        /// <summary>Halftones everything visible in the view that is not in keep (saving the original overrides). Needs an open Transaction.</summary>
        private static void HalftoneExcept(AlignSession s, Document doc, View view, HashSet<ElementId> keep)
        {
            ICollection<ElementId> all = new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType()
                .ToElementIds();
            foreach (ElementId id in all)
            {
                if (keep.Contains(id)) continue;
                try
                {
                    OverrideGraphicSettings o = view.GetElementOverrides(id);
                    if (!s.SavedOverrides.ContainsKey(id)) s.SavedOverrides[id] = o;
                    OverrideGraphicSettings h = view.GetElementOverrides(id);
                    h.SetHalftone(true);
                    view.SetElementOverrides(id, h);
                }
                catch { }
            }
        }

        private static void ApplySectionBox(AlignSession s, Document doc, View view)
        {
            View3D v3 = view as View3D;
            if (v3 == null) return;

            if (!s.SectionSaved)
            {
                s.PrevSectionActive = v3.IsSectionBoxActive;
                s.PrevSectionBox = v3.GetSectionBox();
                s.SectionSaved = true;
            }

            BoundingBoxXYZ focus = ComputeFocusBox(s, doc);
            var box = new BoundingBoxXYZ
            {
                Min = focus.Min,
                Max = focus.Max,
                Transform = Transform.Identity
            };
            v3.SetSectionBox(box);
            v3.IsSectionBoxActive = true;
        }

        private static void ZoomToConflict(AlignSession s, UIDocument uidoc)
        {
            if (uidoc == null || s.Conflict == null) return;
            Document doc = s.Document;
            BoundingBoxXYZ box = ComputeFocusBox(s, doc);

            try
            {
                foreach (UIView uv in uidoc.GetOpenUIViews())
                {
                    if (uv.ViewId.Equals(s.ViewId))
                    {
                        uv.ZoomAndCenterRectangle(box.Min, box.Max);
                        return;
                    }
                }
            }
            catch
            {
                try
                {
                    var ids = new List<ElementId>();
                    if (!s.Conflict.ConflictElementId.Equals(ElementId.InvalidElementId))
                        ids.Add(s.Conflict.ConflictElementId);
                    else
                        ids.Add(s.Conflict.CurveId);
                    uidoc.ShowElements(ids);
                }
                catch { }
            }
        }

        private static BoundingBoxXYZ ComputeFocusBox(AlignSession s, Document doc)
        {
            ConflictInfo c = s.Conflict;
            double x0 = c.Point.X, y0 = c.Point.Y, z0 = c.Point.Z;
            double x1 = x0, y1 = y0, z1 = z0;

            Action<XYZ> inc = pt =>
            {
                x0 = Math.Min(x0, pt.X); y0 = Math.Min(y0, pt.Y); z0 = Math.Min(z0, pt.Z);
                x1 = Math.Max(x1, pt.X); y1 = Math.Max(y1, pt.Y); z1 = Math.Max(z1, pt.Z);
            };

            Element ce = doc.GetElement(c.ConflictElementId);
            if (ce != null)
            {
                BoundingBoxXYZ bb = ce.get_BoundingBox(null);
                if (bb != null) { inc(bb.Min); inc(bb.Max); }
            }

            MEPCurve p = doc.GetElement(c.CurveId) as MEPCurve;
            Line axis = p != null ? GetLine(p) : null;
            if (axis != null)
            {
                XYZ e0 = axis.GetEndPoint(0), e1 = axis.GetEndPoint(1);
                inc(e0.DistanceTo(c.Point) <= e1.DistanceTo(c.Point) ? e0 : e1);
            }

            // Automatic margin: proportional to the size of the conflict area, within sane limits
            double ext = Math.Max(x1 - x0, Math.Max(y1 - y0, z1 - z0));
            double m = ext * Cfg.FocusMarginFactor;
            m = Math.Max(m, Cfg.FocusMarginMinMm * Cfg.MmToFt);
            m = Math.Min(m, Cfg.FocusMarginMaxMm * Cfg.MmToFt);

            return new BoundingBoxXYZ
            {
                Min = new XYZ(x0 - m, y0 - m, z0 - m),
                Max = new XYZ(x1 + m, y1 + m, z1 + m)
            };
        }

        // ------------------------- Restoration -------------------------

        public static void ClearVisuals(AlignSession s, UIDocument uidoc)
        {
            if (!s.VisualsActive && s.SavedOverrides.Count == 0 && !s.SectionSaved) return;

            Document doc = s.Document;
            View view = doc.IsValidObject ? doc.GetElement(s.ViewId) as View : null;

            if (view != null)
            {
                using (var t = new Transaction(doc, "Restore view after clash"))
                {
                    t.Start();
                    try
                    {
                        foreach (var kv in s.SavedOverrides)
                            if (doc.GetElement(kv.Key) != null)
                                view.SetElementOverrides(kv.Key, kv.Value);

                        View3D v3 = view as View3D;
                        if (v3 != null && s.SectionSaved && s.PrevSectionBox != null)
                        {
                            v3.SetSectionBox(s.PrevSectionBox);
                            v3.IsSectionBoxActive = s.PrevSectionActive;
                        }
                        t.Commit();
                    }
                    catch
                    {
                        if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    }
                }
            }

            s.SavedOverrides.Clear();
            s.SectionSaved = false;
            s.PrevSectionBox = null;
            s.VisualsActive = false;

            if (uidoc != null)
            {
                try { uidoc.RefreshActiveView(); } catch { }
            }
        }

        // ------------------------- Utilities -------------------------

        private static OverrideGraphicSettings BuildOverride(Color color, ElementId solidFill)
        {
            var o = new OverrideGraphicSettings();
            o.SetProjectionLineColor(color);
            o.SetCutLineColor(color);
            if (solidFill != null && !solidFill.Equals(ElementId.InvalidElementId))
            {
                o.SetSurfaceForegroundPatternId(solidFill);
                o.SetSurfaceForegroundPatternColor(color);
                o.SetCutForegroundPatternId(solidFill);
                o.SetCutForegroundPatternColor(color);
            }
            return o;
        }

        private static ElementId FindSolidFillId(Document doc)
        {
            FillPatternElement fpe = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
            return fpe != null ? fpe.Id : ElementId.InvalidElementId;
        }

        private static Line GetLine(MEPCurve p)
        {
            LocationCurve lc = p.Location as LocationCurve;
            return lc != null ? lc.Curve as Line : null;
        }

        private static XYZ CenterOf(Element e)
        {
            BoundingBoxXYZ bb = e.get_BoundingBox(null);
            if (bb != null) return bb.Min.Add(bb.Max).Multiply(0.5);
            LocationPoint lp = e.Location as LocationPoint;
            return lp != null ? lp.Point : XYZ.Zero;
        }

        /// <summary>Pipe / conduit fittings: move with the segment.</summary>
        private static bool IsFitting(Element e)
        {
            FamilyInstance fi = e as FamilyInstance;
            if (fi == null || e.Category == null) return false;

            bool inCategory = false;
            foreach (BuiltInCategory bic in Cfg.FittingCategories)
                if (e.Category.Id.Equals(new ElementId(bic))) { inCategory = true; break; }
            if (!inCategory) return false;

            // Junction boxes are also "Conduit Fittings", but act as boundaries: do not move with the segment
            Autodesk.Revit.DB.Mechanical.MechanicalFitting mf = fi.MEPModel as Autodesk.Revit.DB.Mechanical.MechanicalFitting;
            if (mf != null && mf.PartType.ToString() == "JunctionBox") return false;

            return true;
        }

        public static List<Connector> GetConnectors(Element e)
        {
            var list = new List<Connector>();
            if (e == null) return list;

            ConnectorManager cm = null;
            MEPCurve mc = e as MEPCurve;
            if (mc != null) cm = mc.ConnectorManager;
            else
            {
                FamilyInstance fi = e as FamilyInstance;
                if (fi != null && fi.MEPModel != null) cm = fi.MEPModel.ConnectorManager;
            }

            if (cm != null)
                foreach (Connector c in cm.Connectors) list.Add(c);
            return list;
        }

        /// <summary>Only physical connectors support IsConnected / AllRefs (logical or reference connectors throw).</summary>
        private static bool IsPhysical(Connector c)
        {
            try
            {
                ConnectorType t = c.ConnectorType;
                return t == ConnectorType.End || t == ConnectorType.Curve || t == ConnectorType.Physical;
            }
            catch { return false; }
        }

        private static List<Connector> GetRefs(Connector c, Element self)
        {
            var list = new List<Connector>();
            if (!IsPhysical(c)) return list;
            bool connected;
            try { connected = c.IsConnected; } catch { return list; }
            if (!connected) return list;
            foreach (Connector other in c.AllRefs)
            {
                if (other.Owner == null) continue;
                if (other.Owner.Id.Equals(self.Id)) continue;
                if (other.Owner is MEPSystem) continue;
                list.Add(other);
            }
            return list;
        }

        public static string Describe(Document doc, ElementId id)
        {
            Element e = (id == null || id.Equals(ElementId.InvalidElementId)) ? null : doc.GetElement(id);
            if (e == null) return "—";
            string cat = e.Category != null ? e.Category.Name : "Element";
            return cat + " · " + e.Name + " [Id " + id + "]";
        }

        public static string KindText(ConflictKind k)
        {
            switch (k)
            {
                case ConflictKind.Frontier: return "Boundary family in the chain";
                case ConflictKind.NoSlack: return "Adjacent run is too short";
                case ConflictKind.Skew: return "Adjacent run would end up skewed";
                case ConflictKind.Collision: return "Geometric collision";
                case ConflictKind.RevitRejected: return "Revit rejected the adjustment";
                default: return "Geometry cannot be evaluated";
            }
        }
    }

    // ------------------------------------------------------------------------------
    //  Revit Failure / Warning Preprocessor During Alignment
    // ------------------------------------------------------------------------------
    internal sealed class RollbackOnErrorPreprocessor : IFailuresPreprocessor
    {
        public string ErrorText;
        public List<ElementId> FailingIds = new List<ElementId>();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            bool hasError = false;
            foreach (FailureMessageAccessor m in failuresAccessor.GetFailureMessages())
            {
                if (m.GetSeverity() == FailureSeverity.Warning)
                {
                    failuresAccessor.DeleteWarning(m);
                }
                else
                {
                    hasError = true;
                    ErrorText = m.GetDescriptionText();
                    try
                    {
                        foreach (ElementId id in m.GetFailingElementIds())
                            if (!FailingIds.Contains(id)) FailingIds.Add(id);
                    }
                    catch { }
                }
            }
            return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }

    // ------------------------------------------------------------------------------
    //  Command
    // ------------------------------------------------------------------------------
    [Transaction(TransactionMode.Manual)]
    public class AlinearConectorCommand : IExternalCommand
    {
        internal static AlignWindow ActiveWindow;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            if (AlinearConectorCommand.ActiveWindow != null)
            {
                try { AlinearConectorCommand.ActiveWindow.Activate(); return Result.Cancelled; }
                catch { AlinearConectorCommand.ActiveWindow = null; }
            }

            FamilyInstance fam = null;
            Connector conn = null;

            try
            {
                // ---- 1) Family ----
                Reference fr = uidoc.Selection.PickObject(ObjectType.Element, new ConnectorFamilyFilter(),
                    "Select the FAMILY that has the reference connector");
                fam = doc.GetElement(fr.ElementId) as FamilyInstance;

                // ---- 2) Connector ----
                List<Connector> conns = ListConnectors(fam);
                if (conns.Count == 0)
                {
                    TaskDialog.Show("Align to Connector", "The family has no usable connectors.");
                    return Result.Cancelled;
                }

                if (conns.Count == 1) conn = conns[0];   // Single connector: automatic, no pause
                else
                {
                    conn = ChooseConnector(uidoc, doc, fam, conns);
                    if (conn == null) return Result.Cancelled;
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            // From ribbon button a new family/connector is chosen: forget previous segment
            AlignElementsSuite.ConnectorPalette.ActiveCurveId = ElementId.InvalidElementId;

            return ExecuteWithConnector(uidoc, fam, conn, ref message);
        }

        internal static Result ExecuteWithConnector(UIDocument uidoc, FamilyInstance fam, Connector conn, ref string message)
        {
            Document doc = uidoc.Document;

            if (AlinearConectorCommand.ActiveWindow != null)
            {
                try { AlinearConectorCommand.ActiveWindow.Activate(); return Result.Cancelled; }
                catch { AlinearConectorCommand.ActiveWindow = null; }
            }

            var session = new AlignSession { Document = doc };
            ElementId targetCurveId = ElementId.InvalidElementId;

            // REUSE PREVIOUS SEGMENT: only if the palette is open AND still locked to THIS same connector.
            // With another connector (or from the ribbon button) always prompt for the segment.
            if (AlignElementsSuite.ConnectorPalette.Window != null
                && AlignElementsSuite.ConnectorPalette.ActiveFamilyId.Equals(fam.Id)
                && AlignElementsSuite.ConnectorPalette.ActiveConnectorId == conn.Id
                && !AlignElementsSuite.ConnectorPalette.ActiveCurveId.Equals(ElementId.InvalidElementId))
            {
                Element existingElem = doc.GetElement(AlignElementsSuite.ConnectorPalette.ActiveCurveId);
                if (existingElem is MEPCurve && !existingElem.Id.Equals(fam.Id))
                {
                    targetCurveId = existingElem.Id;
                }
            }

            try
            {
                if (targetCurveId.Equals(ElementId.InvalidElementId))
                {
                    // ---- 3) Segment to align (prompt if none remembered) ----
                    Reference tr = uidoc.Selection.PickObject(ObjectType.Element,
                        new MepCurveSelectionFilter(fam.Id),
                        "Select the pipe or conduit to align with the connector");
                    targetCurveId = tr.ElementId;
                }
                session.TargetIds.Add(targetCurveId);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            // Reference is the connector (re-located by Id on each evaluation)
            session.PivotId = fam.Id;
            session.PivotConnectorId = conn.Id;
            session.PivotPointOverride = conn.Origin;
            session.Axis = AlignAxis.Auto;

            // ---- Same engine as Script 1 ----
            bool done;
            try
            {
                done = AlignEngine.ProcessPending(session, uidoc);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                AlignEngine.ClearVisuals(session, uidoc);
                return Result.Failed;
            }

            // Launch / refresh floating palette to keep operating on the same connector and segment
            AlignElementsSuite.ConnectorPalette.ShowFor(uidoc, fam.Id, conn.Id, targetCurveId);

            if (done)
            {
                if (!string.IsNullOrEmpty(session.Note))
                    TaskDialog.Show("Align to Connector", session.Note);
                return Result.Succeeded;
            }

            var handler = new AlignExternalHandler { Session = session };
            ExternalEvent extEvent = ExternalEvent.Create(handler);

            var win = new AlignWindow(session);
            handler.Window = win;
            win.RequestAction = req =>
            {
                handler.Request = req;
                win.SetBusy(true);
                extEvent.Raise();
            };
            win.Closed += (s, e) => AlinearConectorCommand.ActiveWindow = null;

            try { new WindowInteropHelper(win).Owner = Process.GetCurrentProcess().MainWindowHandle; }
            catch { }

            AlinearConectorCommand.ActiveWindow = win;
            win.ShowConflict(session);
            win.Show();

            return Result.Succeeded;
        }

        // ------------------------- Family Connectors -------------------------

        internal static bool IsUsableDomain(Connector c)
        {
            Domain d = c.Domain;
            return d == Domain.DomainPiping ||
                   d == Domain.DomainElectrical ||
                   d == Domain.DomainCableTrayConduit;
        }

        private static List<Connector> ListConnectors(FamilyInstance fam)
        {
            return AlignEngine.GetConnectors(fam).Where(IsUsableDomain).ToList();
        }

        /// <summary>Half connector size: top elevation is Origin.Z + this offset.</summary>
        private static double ConnectorTopOffset(Connector c)
        {
            try
            {
                if (c.Shape == ConnectorProfileType.Round) return c.Radius;
                if (c.Shape == ConnectorProfileType.Rectangular || c.Shape == ConnectorProfileType.Oval)
                    return c.Height / 2.0;
            }
            catch { }
            return 0;
        }

        // ------------------------- Choose Connector (2 or More) -------------------------

        /// <summary>
        /// DirectContext3D markers + PickPoint + KeyConfirmHook. All highlighting and temporary sketch plane live
        /// inside a TransactionGroup that rolls back on exit (leaving the view unchanged).
        /// </summary>
        private static Connector ChooseConnector(UIDocument uidoc, Document doc, FamilyInstance fam, List<Connector> conns)
        {
            View view = doc.ActiveView;
            var server = new ConnectorMarkerServer(
                conns.Select(c => c.Origin).ToList(),
                conns.Select(c => c.CoordinateSystem.BasisZ).ToList());

            BoundingBoxXYZ bb = fam.get_BoundingBox(null);
            double planeZ = bb != null ? (bb.Min.Z + bb.Max.Z) / 2.0 : conns[0].Origin.Z;
            var planeOrigin = new XYZ(conns[0].Origin.X, conns[0].Origin.Y, planeZ);

            Connector chosen = null;
            KeyConfirmHook hook = null;

            using (var tg = new TransactionGroup(doc, "Choose connector"))
            {
                tg.Start();
                try
                {
                    using (var t = new Transaction(doc, "Highlight family / temporary plane"))
                    {
                        t.Start();
                        try
                        {
                            // Temporary sketch plane so PickPoint works in 3D views
                            SketchPlane sp = SketchPlane.Create(doc,
                                Plane.CreateByNormalAndOrigin(XYZ.BasisZ, planeOrigin));
                            view.SketchPlane = sp;
                        }
                        catch { }

                        try
                        {
                            // Highlight family (rolled back when finished)
                            var halfOgs = new OverrideGraphicSettings();
                            halfOgs.SetHalftone(true);
                            var mepIds = new FilteredElementCollector(doc, view.Id)
                                .WhereElementIsNotElementType()
                                .WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory>
                                {
                                    BuiltInCategory.OST_PipeCurves,
                                    BuiltInCategory.OST_Conduit
                                }))
                                .ToElementIds();
                            foreach (ElementId id in mepIds)
                                view.SetElementOverrides(id, halfOgs);

                            var ogs = new OverrideGraphicSettings();
                            ogs.SetProjectionLineColor(new Color(34, 184, 219));
                            ogs.SetSurfaceTransparency(40);
                            view.SetElementOverrides(fam.Id, ogs);
                        }
                        catch { }

                        t.Commit();
                    }

                    server.Register();
                    hook = new KeyConfirmHook();
                    uidoc.RefreshActiveView();

                    while (true)
                    {
                        hook.Confirmed = false;
                        hook.Enabled = server.Selected >= 0;

                        try
                        {
                            XYZ click = uidoc.Selection.PickPoint(ObjectSnapTypes.None,
                                "Click near the connector  ·  Enter / Space = confirm  ·  Esc = cancel");

                            server.Selected = NearestByRay(view, click, conns);
                            uidoc.RefreshActiveView();
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                        {
                            // Enter/Space (hook sends Esc to exit PickPoint) => confirm
                            if (hook.Confirmed && server.Selected >= 0) chosen = conns[server.Selected];
                            break;   // Real Esc => chosen stays null (cancel)
                        }
                    }
                }
                finally
                {
                    if (hook != null) hook.Dispose();
                    server.Unregister();
                    if (tg.GetStatus() == TransactionStatus.Started) tg.RollBack();
                    try { uidoc.RefreshActiveView(); } catch { }
                }
            }

            return chosen;
        }

        /// <summary>
        /// Connector closest to the view RAY passing through the click (avoids temporary-plane
        /// parallax error in 3D views).
        /// </summary>
        private static int NearestByRay(View view, XYZ click, List<Connector> conns)
        {
            XYZ dir;
            View3D v3 = view as View3D;
            if (v3 != null && v3.IsPerspective)
                dir = click.Subtract(v3.GetOrientation().EyePosition);
            else
                dir = view.ViewDirection;

            if (dir.GetLength() < 1e-9) dir = XYZ.BasisZ;
            dir = dir.Normalize();

            int best = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < conns.Count; i++)
            {
                double dist = conns[i].Origin.Subtract(click).CrossProduct(dir).GetLength();
                if (dist < bestDist) { bestDist = dist; best = i; }
            }
            return best;
        }

        // ------------------------- Ghost Cross + Direction -------------------------

        private static bool PickDirection(UIDocument uidoc, Document doc, AlignSession session, XYZ origin, double half)
        {
            View view = doc.ActiveView;
            var server = new GuideServer(origin, half);
            XYZ clicked = null;

            using (var tg = new TransactionGroup(doc, "Temporary guide plane"))
            {
                tg.Start();
                try
                {
                    using (var t = new Transaction(doc, "Temporary plane"))
                    {
                        t.Start();
                        try
                        {
                            SketchPlane sp = SketchPlane.Create(doc,
                                Plane.CreateByNormalAndOrigin(XYZ.BasisZ, origin));
                            view.SketchPlane = sp;
                            t.Commit();
                        }
                        catch
                        {
                            if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                        }
                    }

                    server.Register();
                    uidoc.RefreshActiveView();

                    clicked = uidoc.Selection.PickPoint(ObjectSnapTypes.None,
                        "Click on the guide: horizontal (0°) equalizes Y  |  vertical (90°) equalizes X");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    clicked = null;
                }
                finally
                {
                    server.Unregister();
                    if (tg.GetStatus() == TransactionStatus.Started) tg.RollBack();
                    try { uidoc.RefreshActiveView(); } catch { }
                }
            }

            if (clicked == null) return false;

            double ax = Math.Abs(clicked.X - origin.X);
            double ay = Math.Abs(clicked.Y - origin.Y);
            session.Axis = ax >= ay ? AlignAxis.EqualizeY : AlignAxis.EqualizeX;
            return true;
        }
    }

    // ------------------------------------------------------------------------------
    //  Filter: FamilyInstance with at least 1 Pipe / Conduit / Electrical Connector
    // ------------------------------------------------------------------------------
    internal sealed class ConnectorFamilyFilter : ISelectionFilter
    {
        public bool AllowElement(Element e)
        {
            FamilyInstance fi = e as FamilyInstance;
            if (fi == null || fi.MEPModel == null) return false;

            foreach (Connector c in AlignEngine.GetConnectors(fi))
                if (AlinearConectorCommand.IsUsableDomain(c)) return true;
            return false;
        }
        public bool AllowReference(Reference r, XYZ p) { return false; }
    }

    // ------------------------------------------------------------------------------
    //  DirectContext3D Server: Ghost Markers at Each Connector (Origin + BasisZ Vector)
    // ------------------------------------------------------------------------------
    internal sealed class ConnectorMarkerServer : IDirectContext3DServer
    {
        private readonly Guid _id = Guid.NewGuid();
        private readonly List<XYZ> _origins;
        private readonly List<XYZ> _dirs;
        private MultiServerService _service;

        private VertexBuffer _vb;
        private IndexBuffer _ib;
        private VertexFormat _vf;
        private EffectInstance _ei;
        private int _lineCount;
        private bool _built;
        private int _selected = -1;

        public ConnectorMarkerServer(List<XYZ> origins, List<XYZ> dirs)
        {
            _origins = origins;
            _dirs = dirs;
        }

        /// <summary>Highlighted connector index (-1 = none). Rebuilds buffers on change.</summary>
        public int Selected
        {
            get { return _selected; }
            set { _selected = value; _built = false; }
        }

        public void Register()
        {
            var svc = ExternalServiceRegistry.GetService(
                ExternalServices.BuiltInExternalServices.DirectContext3DService);
            _service = svc as MultiServerService;
            if (_service == null) return;

            _service.AddServer(this);
            IList<Guid> ids = _service.GetActiveServerIds();
            ids.Add(_id);
            _service.SetActiveServers(ids);
        }

        public void Unregister()
        {
            if (_service == null) return;
            try
            {
                IList<Guid> ids = _service.GetActiveServerIds();
                ids.Remove(_id);
                _service.SetActiveServers(ids);
                _service.RemoveServer(_id);
            }
            catch { }
            _service = null;
        }

        private static XYZ Perp(XYZ z)
        {
            XYZ u = z.CrossProduct(XYZ.BasisZ);
            if (u.GetLength() < 1e-3) u = z.CrossProduct(XYZ.BasisX);
            return u.Normalize();
        }

        private void Build()
        {
            var segs = new List<XYZ[]>();
            var cols = new List<ColorWithTransparency>();
            Action<XYZ, XYZ, ColorWithTransparency> add = (a, b, c) => { segs.Add(new[] { a, b }); cols.Add(c); };

            var cyan   = new ColorWithTransparency(34, 184, 219, 0);   // Unselected
            var gray   = new ColorWithTransparency(145, 145, 145, 0);  // Dimmed / non-selected
            var picked = new ColorWithTransparency(0, 210, 90, 0);     // Selected connector

            double minDist = double.MaxValue;
            for (int i = 0; i < _origins.Count; i++)
                for (int j = i + 1; j < _origins.Count; j++)
                    minDist = Math.Min(minDist, _origins[i].DistanceTo(_origins[j]));
            double baseL = minDist < double.MaxValue ? minDist * 0.4 : 120.0 * Cfg.MmToFt;
            baseL = Math.Max(30.0 * Cfg.MmToFt, Math.Min(180.0 * Cfg.MmToFt, baseL));

            for (int i = 0; i < _origins.Count; i++)
            {
                bool sel = i == _selected;
                ColorWithTransparency col = sel ? picked : (_selected >= 0 ? gray : cyan);
                double L = sel ? baseL * 1.35 : baseL;
                double r = 0.4 * L;
                XYZ o = _origins[i];
                XYZ z = _dirs[i].Normalize();

                // Cross at origin
                add(o.Subtract(XYZ.BasisX.Multiply(r)), o.Add(XYZ.BasisX.Multiply(r)), col);
                add(o.Subtract(XYZ.BasisY.Multiply(r)), o.Add(XYZ.BasisY.Multiply(r)), col);
                add(o.Subtract(XYZ.BasisZ.Multiply(r)), o.Add(XYZ.BasisZ.Multiply(r)), col);

                // BasisZ vector (arrow); selected marker draws 3 strands for higher visibility
                XYZ u1 = Perp(z);
                XYZ u2 = z.CrossProduct(u1);
                XYZ tip = o.Add(z.Multiply(L));
                double off = 20.0 * Cfg.MmToFt;
                int kMax = sel ? 1 : 0;
                for (int k = -kMax; k <= kMax; k++)
                {
                    XYZ sh = u1.Multiply(k * off);
                    add(o.Add(sh), tip.Add(sh), col);
                }

                XYZ back = tip.Subtract(z.Multiply(0.25 * L));
                double w = 0.12 * L;
                add(tip, back.Add(u1.Multiply(w)), col);
                add(tip, back.Subtract(u1.Multiply(w)), col);
                add(tip, back.Add(u2.Multiply(w)), col);
                add(tip, back.Subtract(u2.Multiply(w)), col);
            }

            _lineCount = segs.Count;
            int vCount = _lineCount * 2;

            int vSize = vCount * VertexPositionColored.GetSizeInFloats();
            _vf = new VertexFormat(VertexFormatBits.PositionColored);
            _vb = new VertexBuffer(vSize);
            _vb.Map(vSize);
            VertexStreamPositionColored vs = _vb.GetVertexStreamPositionColored();
            for (int i = 0; i < _lineCount; i++)
            {
                vs.AddVertex(new VertexPositionColored(segs[i][0], cols[i]));
                vs.AddVertex(new VertexPositionColored(segs[i][1], cols[i]));
            }
            _vb.Unmap();

            int iSize = _lineCount * IndexLine.GetSizeInShortInts();
            _ib = new IndexBuffer(iSize);
            _ib.Map(iSize);
            IndexStreamLine ist = _ib.GetIndexStreamLine();
            for (int i = 0; i < _lineCount; i++)
                ist.AddLine(new IndexLine(2 * i, 2 * i + 1));
            _ib.Unmap();

            _ei = new EffectInstance(VertexFormatBits.PositionColored);
            _built = true;
        }

        // ---- IDirectContext3DServer ----
        public void RenderScene(Autodesk.Revit.DB.View dBView, DisplayStyle displayStyle)
        {
            try
            {
                if (!_built) Build();
                DrawContext.FlushBuffer(_vb, _lineCount * 2, _ib, _lineCount * 2, _vf, _ei,
                    PrimitiveType.LineList, 0, _lineCount);
            }
            catch { }
        }

        public bool CanExecute(Autodesk.Revit.DB.View dBView) { return true; }
        public bool UseInTransparentPass(Autodesk.Revit.DB.View dBView) { return false; }

        public Outline GetBoundingBox(Autodesk.Revit.DB.View dBView)
        {
            double pad = 3.0;   // ~1 m
            double x0 = _origins.Min(p => p.X) - pad, y0 = _origins.Min(p => p.Y) - pad, z0 = _origins.Min(p => p.Z) - pad;
            double x1 = _origins.Max(p => p.X) + pad, y1 = _origins.Max(p => p.Y) + pad, z1 = _origins.Max(p => p.Z) + pad;
            return new Outline(new XYZ(x0, y0, z0), new XYZ(x1, y1, z1));
        }

        public Guid GetServerId() { return _id; }
        public ExternalServiceId GetServiceId() { return ExternalServices.BuiltInExternalServices.DirectContext3DService; }
        public string GetName() { return "PipeAligner connector markers"; }
        public string GetVendorId() { return "PipeAligner"; }
        public string GetDescription() { return "Ghost markers for connectors."; }
        public string GetApplicationId() { return ""; }
        public string GetSourceId() { return ""; }
        public bool UsesHandles() { return false; }
    }
}
