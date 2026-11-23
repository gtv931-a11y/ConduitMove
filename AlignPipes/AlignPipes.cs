// =====================================================================================
//  AlignPipes.cs   (v4)  -  "Align Conduits / Pipes"
//
//  Contents (namespace PipeAligner):
//   1) AlinearTuberiasCommand : IExternalCommand -> selection, halftone, launches the engine
//        (the class name is kept so existing .addin / ribbon registrations keep working;
//         the VISIBLE command name must be "Align Conduits / Pipes" in your button / .addin Text)
//   2) AlignEngine / AlignWindow / AlignExternalHandler / GuideServer (DirectContext3D)
//   3) AxisTogglePalette / AxisToggleWindow : floating palette next to the moved segment
//
//  Flow:
//   - 1 click: PIVOT (Pipe or Conduit).
//   - Successive clicks: segments to align (click again on a chosen one to remove it).
//     Enter / Space confirms, Esc cancels. While picking, EVERYTHING visible except the pivot
//     and the picked segments is in Halftone.
//   - Automatic axis from the pivot -> segments vector.
//   - With ONE segment a floating palette appears next to the moved segment. Only a CLICK on its
//     button switches axis. Enter / Space accepts the current result and closes the palette.
//   - 3-level evaluation: chain (neighbour runs) -> domino effect on boundary families -> collision.
//   - Neighbouring conduits / pipes / fittings never raise a "skew" error; only a real collapse
//     of an adjacent run (zero / inverted length) or a Revit rejection is reported.
//
//  References in the .csproj: RevitAPI, RevitAPIUI, PresentationCore, PresentationFramework,
//  WindowsBase, System.Xaml.   C# 7.3 compatible.
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.ExternalService;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace PipeAligner
{
    // ------------------------------------------------------------------------------
    //  Configuration
    // ------------------------------------------------------------------------------
    internal static class Cfg
    {
        public const string AppTitle = "Align Conduits / Pipes";

        public const double MmToFt = 1.0 / 304.8;

        /// <summary>Minimum geometric length an adjacent run must keep (below this it is a real collapse).</summary>
        public const double MinNeighbourLengthMm = 1.0;

        public const double AlignTolFt = 0.1 * MmToFt;       // below this: "already aligned"
        public const double SweepEndShrinkMm = 5.0;          // shrinks the sweep solid at both ends

        public const double FocusMarginMinMm = 500.0;        // automatic focus margin limits
        public const double FocusMarginMaxMm = 5000.0;
        public const double FocusMarginFactor = 0.4;

        public const double GuideMinHalfLengthM = 500.0;
        public const double GuideExtraMarginM = 100.0;

        /// <summary>
        /// UIView.GetWindowRectangle() is interpreted as client coordinates of the Revit main window.
        /// Set to true if, on your build, it already returns screen coordinates.
        /// </summary>
        public const bool ViewRectIsScreenSpace = false;

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

        /// <summary>Accessories that MOVE with the segment (elbows, tees, unions).</summary>
        public static readonly BuiltInCategory[] FittingCategories =
        {
            BuiltInCategory.OST_PipeFitting,
            BuiltInCategory.OST_ConduitFitting
        };
    }

    // ------------------------------------------------------------------------------
    //  Native helpers
    // ------------------------------------------------------------------------------
    internal static class NativeWin
    {
        public static IntPtr MainHwnd = IntPtr.Zero;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        /// <summary>Pixels per device-independent unit (1.0 = 96 dpi).</summary>
        public static double Scale()
        {
            try
            {
                if (MainHwnd != IntPtr.Zero)
                {
                    uint d = GetDpiForWindow(MainHwnd);
                    if (d >= 48) return d / 96.0;
                }
            }
            catch { }
            return 1.0;
        }

        public static IntPtr Hwnd()
        {
            if (MainHwnd != IntPtr.Zero) return MainHwnd;
            try { return Process.GetCurrentProcess().MainWindowHandle; }
            catch { return IntPtr.Zero; }
        }
    }

    // ------------------------------------------------------------------------------
    //  Data models
    // ------------------------------------------------------------------------------

    /// <summary>EqualizeY = guide 0° (equalizes Y, keeps X and Z). EqualizeX = guide 90° (equalizes X, keeps Y and Z).</summary>
    internal enum AlignAxis { EqualizeY, EqualizeX }

    internal enum FrontierPolicy { Disconnect, MoveWith }

    internal enum ConflictKind
    {
        Frontier,          // the chain touches a boundary family (box, equipment, device...)
        NoSlack,           // an adjacent run would collapse (zero / inverted length)
        Collision,         // the sweep hits another element
        RevitRejected,     // Revit rejected the change
        InvalidGeometry    // geometry cannot be evaluated
    }

    internal enum AlignRequest { Retry, DisconnectAndAlign, MoveWithFamily, Cleanup }

    internal sealed class ConflictInfo
    {
        public ConflictKind Kind;
        public XYZ Point;
        public ElementId CurveId;
        public ElementId ConflictElementId = ElementId.InvalidElementId;
        public HashSet<ElementId> ChainIds = new HashSet<ElementId>();
        public string Message;
        public int RiskCount;        // domino effect: branches that would compromise another fixed element
        public string RiskText = "";
    }

    internal sealed class DominoRisk
    {
        public Connector OnFamily;     // boundary family connector leaving towards the compromised branch
        public Connector FirstOther;   // immediate connector on the other side (for DisconnectFrom)
        public Element Blocker;
        public XYZ Point;
        public string Reason;
    }

    internal sealed class FrontierInfo
    {
        public Element Family;
        public Connector ChainSide;    // connector on the chain side (pipe or fitting)
        public Connector FamilySide;   // family connector
        /// <summary>Every chain/family connector pair (a family can touch the chain through several connectors).</summary>
        public List<Connector[]> Pairs = new List<Connector[]>();
        public List<DominoRisk> Risks = new List<DominoRisk>();
    }

    internal sealed class ChainResult
    {
        public MEPCurve Curve;
        public XYZ Displacement = XYZ.Zero;
        public bool AlreadyAligned;
        public HashSet<ElementId> ChainIds = new HashSet<ElementId>();
        public HashSet<ElementId> FrontierIds = new HashSet<ElementId>();
        /// <summary>The segment plus every connected conduit / pipe fitting (they all travel together).</summary>
        public HashSet<ElementId> MoverIds = new HashSet<ElementId>();
        public List<FrontierInfo> Frontiers = new List<FrontierInfo>();
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
        public AlignAxis Axis;
        public List<ElementId> TargetIds = new List<ElementId>();
        public int NextIndex;

        /// <summary>User decision for each boundary family.</summary>
        public Dictionary<ElementId, FrontierPolicy> Policies = new Dictionary<ElementId, FrontierPolicy>();

        /// <summary>"Disconnect and Align" on a non-boundary conflict: every connected family is detached from the chain.</summary>
        public bool DisconnectAll;

        public ConflictInfo Conflict;

        // Previous state saved so it can be restored
        public ElementId ViewId = ElementId.InvalidElementId;
        public Dictionary<ElementId, OverrideGraphicSettings> SavedOverrides =
            new Dictionary<ElementId, OverrideGraphicSettings>();
        public bool SectionSaved;
        public bool PrevSectionActive;
        public BoundingBoxXYZ PrevSectionBox;
        public bool VisualsActive;

        // Elements that moved / adjusted in the last successful alignment (kept un-halftoned in the palette phase)
        public HashSet<ElementId> MovedIds = new HashSet<ElementId>();

        // Axis switching (single segment only)
        public AlignAxis UsedAxis;            // axis actually used in the last alignment
        public XYZ OrigP0, OrigP1;            // original segment position
        public XYZ AlignedP0, AlignedP1;      // position after aligning
        public bool FamilyMoved;              // a boundary family moved: cannot be undone
    }

    // ------------------------------------------------------------------------------
    //  1) Command
    // ------------------------------------------------------------------------------
    [Transaction(TransactionMode.Manual)]
    public class AlinearTuberiasCommand : IExternalCommand
    {
        internal static AlignWindow ActiveWindow;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try { NativeWin.MainHwnd = commandData.Application.MainWindowHandle; }
            catch { }

            if (ActiveWindow != null)
            {
                try { ActiveWindow.Activate(); return Result.Cancelled; }
                catch { ActiveWindow = null; }
            }

            AxisTogglePalette.CloseAndRestore(uidoc);

            var session = new AlignSession { Document = doc };

            bool userCancelled = false;
            using (var selTg = new TransactionGroup(doc, "PipeAligner temporary selection"))
            {
                selTg.Start();
                try
                {
                    // ---- Pivot (1 click) ----
                    Reference pr = uidoc.Selection.PickObject(ObjectType.Element,
                        new MepCurveSelectionFilter(ElementId.InvalidElementId),
                        "Select the PIVOT (pipe or conduit)");
                    session.PivotId = pr.ElementId;

                    // Everything visible goes to Halftone, except the pivot (and later the picked segments)
                    View activeView = doc.ActiveView;
                    Color pivotColor = new Color(34, 184, 219);
                    Color targetColor = new Color(0, 190, 90);

                    using (var t = new Transaction(doc, "Halftone selection"))
                    {
                        t.Start();
                        ICollection<ElementId> allIds = new FilteredElementCollector(doc, activeView.Id)
                            .WhereElementIsNotElementType()
                            .ToElementIds();
                        foreach (ElementId id in allIds)
                        {
                            if (!id.Equals(session.PivotId))
                                AlignEngine.TrySetHalftone(activeView, id, true);
                        }
                        AlignEngine.TrySetTint(activeView, session.PivotId, pivotColor);
                        t.Commit();
                    }

                    // ---- Targets (1..N): Enter or Space confirms; Esc cancels everything ----
                    var filter = new MepCurveSelectionFilter(session.PivotId);
                    using (var keyHook = new KeyConfirmHook())
                    {
                        while (true)
                        {
                            try
                            {
                                Reference r = uidoc.Selection.PickObject(ObjectType.Element, filter,
                                    "Click to add/remove segments (" + session.TargetIds.Count +
                                    " selected). Enter/Space = continue | Esc = cancel");

                                bool added;
                                if (session.TargetIds.Contains(r.ElementId))
                                {
                                    session.TargetIds.Remove(r.ElementId);
                                    added = false;
                                }
                                else
                                {
                                    session.TargetIds.Add(r.ElementId);
                                    added = true;
                                }

                                using (var t = new Transaction(doc, "Mark segment"))
                                {
                                    t.Start();
                                    if (added) AlignEngine.TrySetTint(activeView, r.ElementId, targetColor);
                                    else AlignEngine.TrySetHalftone(activeView, r.ElementId, true);
                                    t.Commit();
                                }
                            }
                            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                            {
                                if (keyHook.Confirmed)
                                    break;
                                userCancelled = true;
                                break;
                            }
                        }
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    userCancelled = true;
                }
                finally
                {
                    // Always restores the original graphics (confirm, cancel or error)
                    if (selTg.GetStatus() == TransactionStatus.Started)
                        selTg.RollBack();
                }
            }

            if (userCancelled)
                return Result.Cancelled;

            if (session.TargetIds.Count == 0)
            {
                TaskDialog.Show(Cfg.AppTitle, "No segment was selected for alignment.");
                return Result.Cancelled;
            }

            // ---- Automatic axis ----
            if (!ChooseAxisAuto(doc, session)) return Result.Cancelled;

            // Original position (to switch axis when there is a single segment)
            if (session.TargetIds.Count == 1)
            {
                LocationCurve olc = doc.GetElement(session.TargetIds[0]).Location as LocationCurve;
                Line ol = olc != null ? olc.Curve as Line : null;
                if (ol != null)
                {
                    session.OrigP0 = ol.GetEndPoint(0);
                    session.OrigP1 = ol.GetEndPoint(1);
                }
            }

            // ---- First evaluation ----
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

            if (done)
            {
                AxisTogglePalette.ShowIfApplicable(uidoc, session);
                return Result.Succeeded;
            }

            // ---- Modeless window ----
            var handler = new AlignExternalHandler { Session = session };
            ExternalEvent extEvent = ExternalEvent.Create(handler);

            var win = new AlignWindow();
            handler.Window = win;
            win.RequestAction = req =>
            {
                handler.Request = req;
                win.SetBusy(true);
                extEvent.Raise();
            };
            win.Closed += (s, e) => ActiveWindow = null;

            try { new WindowInteropHelper(win).Owner = NativeWin.Hwnd(); }
            catch { }

            ActiveWindow = win;
            win.ShowConflict(session);
            win.Show();

            return Result.Succeeded;
        }

        /// <summary>
        /// Picks the axis without asking for a click: mean of the pivot -> segments vector (absolute value).
        /// X dominates (under 45°) -> guide 0°: equalizes Y.  Y dominates -> guide 90°: equalizes X.
        /// </summary>
        private static bool ChooseAxisAuto(Document doc, AlignSession session)
        {
            XYZ c = AlignEngine.PivotCenter(doc, session);
            if (c == null)
            {
                TaskDialog.Show(Cfg.AppTitle, "The pivot geometry could not be read.");
                return false;
            }

            double sx = 0, sy = 0;
            foreach (ElementId id in session.TargetIds)
            {
                XYZ m = AlignEngine.MidPoint(doc.GetElement(id));
                if (m == null) continue;
                sx += Math.Abs(m.X - c.X);
                sy += Math.Abs(m.Y - c.Y);
            }

            session.Axis = sx >= sy ? AlignAxis.EqualizeY : AlignAxis.EqualizeX;
            session.UsedAxis = session.Axis;
            return true;
        }
    }

    // ------------------------------------------------------------------------------
    //  Temporary keyboard hook: Enter or Space confirm the selection by sending Esc
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
        public bool Confirmed;

        public KeyConfirmHook()
        {
            _proc = Callback;
            _hook = SetWindowsHookEx(WH_KEYBOARD, _proc, IntPtr.Zero, GetCurrentThreadId());
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int vk = wParam.ToInt32();
                bool isKeyDown = (lParam.ToInt64() & 0x80000000L) == 0;
                if (isKeyDown && (vk == VK_RETURN || vk == VK_SPACE))
                {
                    Confirmed = true;
                    try
                    {
                        IntPtr hwnd = NativeWin.Hwnd();
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
    //  Palette keyboard hook: Enter / Space accept the current result
    // ------------------------------------------------------------------------------
    internal sealed class PaletteKeyHook : IDisposable
    {
        private const int WH_KEYBOARD = 2;
        private const int VK_RETURN = 0x0D;
        private const int VK_SPACE = 0x20;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private readonly IntPtr _hook;
        private readonly HookProc _proc;
        private readonly Action _onConfirm;
        private bool _fired;

        /// <summary>While true the keys pass through untouched (e.g. while a TaskDialog is shown).</summary>
        public bool Suspended;

        public PaletteKeyHook(Action onConfirm)
        {
            _onConfirm = onConfirm;
            _proc = Callback;
            _hook = SetWindowsHookEx(WH_KEYBOARD, _proc, IntPtr.Zero, GetCurrentThreadId());
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !Suspended)
            {
                int vk = wParam.ToInt32();
                if (vk == VK_RETURN || vk == VK_SPACE)
                {
                    bool isKeyDown = (lParam.ToInt64() & 0x80000000L) == 0;
                    if (isKeyDown && !_fired)
                    {
                        _fired = true;
                        try { if (_onConfirm != null) _onConfirm(); }
                        catch { }
                    }
                    return (IntPtr)1;   // swallow (Revit would otherwise repeat the last command on Enter)
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
    //  Selection filter: accepts pipe or conduit
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
    //  DirectContext3D server: ghost cross (does not create model elements)
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

            segs.Add(new[]
            {
                new XYZ(_origin.X - _half, _origin.Y, _origin.Z),
                new XYZ(_origin.X + _half, _origin.Y, _origin.Z)
            });
            cols.Add(guideColor);

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
    //  API bridge (ExternalEvent)
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
                }

                if (closing && req != AlignRequest.Cleanup)
                    AxisTogglePalette.ShowIfApplicable(uidoc, Session);
            }
            catch (Exception ex)
            {
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

        public string GetName() { return "PipeAligner.ExternalHandler"; }
    }

    // ------------------------------------------------------------------------------
    //  Conflict window (dark slate, code-only WPF, text-only buttons, always enabled)
    // ------------------------------------------------------------------------------
    internal sealed class AlignWindow : Window
    {
        private readonly TextBlock _title = new TextBlock();
        private readonly TextBlock _detail = new TextBlock();
        private readonly Button _retry = new Button();
        private readonly Button _disc = new Button();
        private readonly Button _move = new Button();
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

        public AlignWindow()
        {
            Title = Cfg.AppTitle;
            Width = 460;
            MaxHeight = 700;
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
                Text = Cfg.AppTitle,
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
            _close.Content = "Cancel / Close";
            foreach (Button b in new[] { _retry, _disc, _move, _close })
                b.Margin = new Thickness(0, 0, 0, 6);

            _retry.Click += (s, e) => Fire(AlignRequest.Retry);
            _disc.Click += (s, e) => Fire(AlignRequest.DisconnectAndAlign);
            _move.Click += (s, e) => Fire(AlignRequest.MoveWithFamily);
            _close.Click += (s, e) => Close();

            var body = new StackPanel { Margin = new Thickness(12) };
            body.Children.Add(_title);
            body.Children.Add(scroll);
            body.Children.Add(_retry);
            body.Children.Add(_disc);
            body.Children.Add(_move);
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
                detail += "\n\nResolve the conflict in Revit and press Recalculate, " +
                          "or use 'Disconnect and Align' to detach the conflicting element from the chain.";
            }

            SetStatus(title, detail);
        }
    }

    // ------------------------------------------------------------------------------
    //  2) Engine
    // ------------------------------------------------------------------------------
    internal static class AlignEngine
    {
        // ------------------------- Type detection -------------------------

        public static bool IsPipe(Element e) { return e is Pipe; }

        public static bool IsConduit(Element e) { return e is Conduit; }

        /// <summary>An adjacent run only absorbs the movement if it is the same kind.</summary>
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

        // ------------------------- View graphics helpers -------------------------

        /// <summary>Turns Halftone on/off for one element in the view, keeping its other overrides. Needs an open Transaction.</summary>
        public static bool TrySetHalftone(View view, ElementId id, bool on)
        {
            try
            {
                OverrideGraphicSettings o = view.GetElementOverrides(id);
                o.SetHalftone(on);
                view.SetElementOverrides(id, o);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Normal (non-halftone) display with a tinted projection line. Needs an open Transaction.</summary>
        public static bool TrySetTint(View view, ElementId id, Color color)
        {
            try
            {
                OverrideGraphicSettings o = view.GetElementOverrides(id);
                o.SetHalftone(false);
                o.SetProjectionLineColor(color);
                view.SetElementOverrides(id, o);
                return true;
            }
            catch { return false; }
        }

        private static void SaveOverride(AlignSession s, View view, ElementId id)
        {
            if (s.SavedOverrides.ContainsKey(id)) return;
            try { s.SavedOverrides[id] = view.GetElementOverrides(id); }
            catch { }
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
                SaveOverride(s, view, id);
                TrySetHalftone(view, id, true);
            }
        }

        // ------------------------- Main flow -------------------------

        /// <summary>
        /// Processes the pending segments in order. Stops at the first conflict
        /// (highlights it and returns false). Returns true if everything was aligned.
        /// </summary>
        public static bool ProcessPending(AlignSession s, UIDocument uidoc)
        {
            Document doc = s.Document;
            s.Conflict = null;

            while (s.NextIndex < s.TargetIds.Count)
            {
                MEPCurve p = doc.GetElement(s.TargetIds[s.NextIndex]) as MEPCurve;
                if (p == null) { s.NextIndex++; continue; }

                ChainResult r = Evaluate(doc, s, p);

                if (r.AlreadyAligned) { s.NextIndex++; continue; }

                if (r.Conflict == null)
                {
                    // Tries the movement strategies in order; each failed attempt is rolled back completely.
                    ConflictInfo firstFailure = null;
                    bool applied = false;
                    for (int mode = 0; mode < 3 && !applied; mode++)
                    {
                        ConflictInfo c = ApplyAlignment(doc, s, r, mode);
                        if (c == null) { applied = true; break; }
                        if (firstFailure == null) firstFailure = c;
                        if (c.Kind != ConflictKind.RevitRejected) { firstFailure = c; break; }
                    }
                    r.Conflict = applied ? null : firstFailure;
                    if (r.Conflict == null)
                        foreach (ElementId id in r.ChainIds) s.MovedIds.Add(id);
                }

                if (r.Conflict != null)
                {
                    s.Conflict = r.Conflict;
                    HighlightConflict(s, uidoc);
                    return false;
                }

                s.NextIndex++;
            }
            return true;
        }

        // ------------------------- Displacement (according to the chosen guide) -------------------------

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
            return MidPoint(doc.GetElement(s.PivotId));
        }

        /// <summary>
        /// EqualizeY: keeps X and Z, equalizes Y with the pivot.  EqualizeX: keeps Y and Z, equalizes X.
        /// If the segment is parallel to the movement direction it cannot be aligned that way.
        /// </summary>
        private static XYZ ComputeDisplacement(Document doc, AlignSession s, Line axis, out string error)
        {
            error = null;
            XYZ c = PivotCenter(doc, s);
            if (c == null) { error = "The pivot could not be read."; return XYZ.Zero; }

            XYZ mid = axis.Evaluate(0.5, true);
            bool useY = s.Axis == AlignAxis.EqualizeY;
            XYZ dir = useY ? XYZ.BasisY : XYZ.BasisX;

            if (Math.Abs(axis.Direction.DotProduct(dir)) > 0.999)
            {
                useY = !useY;
                dir = useY ? XYZ.BasisY : XYZ.BasisX;
                if (Math.Abs(axis.Direction.DotProduct(dir)) > 0.999)
                {
                    error = "The segment is vertical and there is no plan direction that aligns it with the pivot.";
                    return XYZ.Zero;
                }
            }

            s.UsedAxis = useY ? AlignAxis.EqualizeY : AlignAxis.EqualizeX;
            double delta = useY ? c.Y - mid.Y : c.X - mid.X;
            return dir.Multiply(delta);
        }

        // ------------------------- Chain evaluation (3 levels) -------------------------

        private enum NbResult { Ok, NoGeometry, Collapse }

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
            XYZ d = ComputeDisplacement(doc, s, axis, out err);
            if (err != null)
            {
                r.Fail(ConflictKind.InvalidGeometry, CenterOf(p), p.Id, err);
                return r;
            }
            r.Displacement = d;
            if (d.GetLength() < Cfg.AlignTolFt) { r.AlreadyAligned = true; return r; }

            // ===== Pass 1: main chain through connectors =====
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

                        if (IsFitting(o))
                        {
                            movers.Add(o.Id);
                            r.ChainIds.Add(o.Id);
                            queue.Enqueue(o);
                        }
                        else if (o is MEPCurve && SameKind(p, o))
                        {
                            List<XYZ> list;
                            if (!neighbours.TryGetValue(o.Id, out list))
                            {
                                list = new List<XYZ>();
                                neighbours[o.Id] = list;
                            }
                            list.Add(other.Origin);
                        }
                        else
                        {
                            // Boundary family (box, device, equipment, junction box...). Every connector pair is kept.
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

            r.MoverIds = movers;

            // Adjacent conduits / pipes are part of the active chain (shown orange, adjusted with the move)
            foreach (ElementId nid in neighbours.Keys) r.ChainIds.Add(nid);

            // ===== Pass 2: boundary families + domino effect =====
            foreach (FrontierInfo fi in r.Frontiers)
            {
                AnalyzeDomino(doc, p, d, fi, r.ChainIds);

                if (s.DisconnectAll || s.Policies.ContainsKey(fi.Family.Id)) continue;   // the user already decided

                ConflictInfo cf = r.Fail(ConflictKind.Frontier, fi.FamilySide.Origin, fi.Family.Id,
                    "The chain touches a boundary family. Choose 'Disconnect and Align' (aligns only the segment) " +
                    "or 'Move with Element' (the family moves together with the chain).");
                cf.RiskCount = fi.Risks.Count;
                cf.RiskText = string.Join("\n", fi.Risks.Select(k =>
                    "• " + Describe(doc, k.Blocker == null ? ElementId.InvalidElementId : k.Blocker.Id) +
                    " — " + k.Reason));
                return r;
            }

            // ===== Adjacent runs: only a REAL collapse blocks (no skew errors) =====
            foreach (var kv in neighbours)
            {
                MEPCurve q = doc.GetElement(kv.Key) as MEPCurve;
                if (q == null) continue;

                if (kv.Value.Count > 1) continue;   // both ends move: it is only translated

                XYZ near;
                double amount;
                NbResult nb = CheckNeighbour(q, kv.Value[0], d, out near, out amount);
                if (nb != NbResult.Collapse) continue;

                r.Fail(ConflictKind.NoSlack, near, q.Id,
                    "An adjacent run would collapse or invert (it is missing " +
                    (amount / Cfg.MmToFt).ToString("0.#") + " mm of length to follow the movement).");
                return r;
            }

            // ===== Pass 3: physical collision =====
            ConflictInfo col = CheckCollisions(doc, s, p, axis, d, r);
            if (col != null) r.Conflict = col;

            return r;
        }

        /// <summary>
        /// The adjacent run keeps its far end fixed and its near end follows the movement.
        /// A change of angle is NOT an error (Revit adjusts it); only a collapsed or inverted run is.
        /// </summary>
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

            double minLen = Cfg.MinNeighbourLengthMm * Cfg.MmToFt;
            double len2 = v2.GetLength();
            bool flipped = v2.DotProduct(v) <= 0;
            if (flipped || len2 < minLen)
            {
                amountFt = flipped ? len2 + minLen : minLen - len2;
                return NbResult.Collapse;
            }
            return NbResult.Ok;
        }

        /// <summary>
        /// Domino effect: leaves through the OTHER connectors of the boundary family and checks whether moving
        /// it would drag a third fixed element or only stretch free runs.
        /// </summary>
        private static void AnalyzeDomino(Document doc, MEPCurve p, XYZ d, FrontierInfo fi, HashSet<ElementId> chainIds)
        {
            var visited = new HashSet<ElementId>(chainIds) { fi.Family.Id };

            foreach (Connector fc in GetConnectors(fi.Family))
            {
                bool isJoint = false;
                foreach (Connector[] pr in fi.Pairs)
                    if (pr[1].Id == fc.Id) { isJoint = true; break; }
                if (isJoint) continue;   // joints with our chain

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
                    // Elbows / tees: the branch continues (they move with the family)
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
                    if (nb == NbResult.Collapse)
                        return new DominoRisk
                        {
                            OnFamily = fc,
                            FirstOther = first,
                            Blocker = o,
                            Point = near,
                            Reason = "the run does not have enough length to absorb the movement"
                        };
                }
                else
                {
                    // Another fixed family connected: moving it would drag it along
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

        // ------------------------- Collision (sweep) -------------------------

        private static ConflictInfo CheckCollisions(Document doc, AlignSession s, MEPCurve p, Line axis, XYZ d, ChainResult r)
        {
            double radius = GetRadius(p);
            XYZ u = axis.Direction;

            double along = d.DotProduct(u);
            XYZ dPerp = d.Subtract(u.Multiply(along));
            double dl = dPerp.GetLength();
            if (dl < 1e-6) return null;                             // it only slides along its own axis

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

            // Excluded: the chain, the boundaries (already analysed) and the other targets (they also move)
            var exclude = new HashSet<ElementId>(r.ChainIds);
            foreach (ElementId id in r.FrontierIds) exclude.Add(id);
            foreach (ElementId id in s.TargetIds) exclude.Add(id);
            exclude.Add(p.Id);

            // The pivot (and fittings attached to it) is not a clash: aligning with an orthogonal or
            // vertical pipe leaves the segment crossing it by definition.
            exclude.Add(s.PivotId);
            Element pivotEl = doc.GetElement(s.PivotId);
            if (pivotEl != null)
                foreach (Connector pc in GetConnectors(pivotEl))
                    foreach (Connector po in GetRefs(pc, pivotEl))
                        if (po.Owner != null && IsFitting(po.Owner)) exclude.Add(po.Owner.Id);

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
                if (el is MEPCurve || IsFitting(el)) continue;   // conduits / pipes / fittings never block
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

        // ------------------------- Apply the alignment (single Transaction) -------------------------

        /// <summary>
        /// mode 0: move the segment like Revit's own Move / drag (connected fittings and runs follow).
        /// mode 1: move the segment and all its fittings together as one group.
        /// mode 2: reassign the segment's location curve.
        /// </summary>
        private static ConflictInfo ApplyAlignment(Document doc, AlignSession s, ChainResult r, int mode)
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

                    // 1) User decisions about boundary families
                    bool movedFamily = false;
                    foreach (FrontierInfo fi in r.Frontiers)
                    {
                        FrontierPolicy pol;
                        if (!s.Policies.TryGetValue(fi.Family.Id, out pol))
                        {
                            if (!s.DisconnectAll) continue;
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
                            // Disconnects the branches that would compromise a third fixed element
                            foreach (DominoRisk risk in fi.Risks)
                                risk.OnFamily.DisconnectFrom(risk.FirstOther);

                            ElementTransformUtils.MoveElement(doc, fi.Family.Id, r.Displacement);
                            movedFamily = true;
                            s.FamilyMoved = true;
                        }
                    }

                    // 2) Align the segment (if the family moved, what is left is recomputed)
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
                    {
                        if (mode == 0)
                        {
                            ElementTransformUtils.MoveElement(doc, p.Id, d);
                        }
                        else if (mode == 1)
                        {
                            ElementTransformUtils.MoveElements(doc, new List<ElementId>(r.MoverIds), d);
                        }
                        else
                        {
                            LocationCurve lc = (LocationCurve)p.Location;
                            Line old = (Line)lc.Curve;
                            // Reassigning the curve makes Revit adjust connected fittings and adjacent runs
                            lc.Curve = Line.CreateBound(
                                old.GetEndPoint(0).Add(d),
                                old.GetEndPoint(1).Add(d));
                        }
                    }

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

        // ------------------------- Highlight + Halftone + Section Box -------------------------

        /// <summary>Conflict element + active chain + everything directly connected to the conflict element.</summary>
        private static HashSet<ElementId> BuildConflictKeepSet(Document doc, ConflictInfo c)
        {
            var keep = new HashSet<ElementId>(c.ChainIds);
            keep.Add(c.CurveId);
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

                    // 1) Halftone for everything NOT connected to the conflict (kept until the clash is resolved)
                    HashSet<ElementId> keep = BuildConflictKeepSet(doc, c);
                    HalftoneExcept(s, doc, view, keep);

                    // 2) Save previous state of the highlighted elements
                    var toSave = new List<ElementId>(orangeIds);
                    if (!c.ConflictElementId.Equals(ElementId.InvalidElementId)) toSave.Add(c.ConflictElementId);
                    foreach (ElementId id in toSave)
                    {
                        if (doc.GetElement(id) == null) continue;
                        SaveOverride(s, view, id);
                    }

                    // 3) Orange: segment + chain. Red: boundary / obstacle (normal display, no halftone)
                    foreach (ElementId id in orangeIds)
                        if (doc.GetElement(id) != null) view.SetElementOverrides(id, orange);

                    if (!c.ConflictElementId.Equals(ElementId.InvalidElementId) &&
                        doc.GetElement(c.ConflictElementId) != null)
                        view.SetElementOverrides(c.ConflictElementId, red);

                    // 4) Section Box (3D only), automatically sized towards the current conflict
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

        /// <summary>
        /// Palette phase: keeps Halftone on everything except the moved chain, the pivot and the targets,
        /// so the user can judge the result.
        /// </summary>
        public static void ApplyFocusHalftone(AlignSession s, UIDocument uidoc)
        {
            Document doc = s.Document;
            View view = doc.ActiveView;
            s.ViewId = view.Id;

            var keep = new HashSet<ElementId>(s.MovedIds);
            keep.Add(s.PivotId);
            foreach (ElementId id in s.TargetIds) keep.Add(id);

            using (var t = new Transaction(doc, "Halftone focus"))
            {
                t.Start();
                try
                {
                    HalftoneExcept(s, doc, view, keep);
                    t.Commit();
                    s.VisualsActive = true;
                }
                catch
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    s.SavedOverrides.Clear();
                }
            }

            if (uidoc != null)
            {
                try { uidoc.RefreshActiveView(); } catch { }
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

        /// <summary>Focus box around the current conflict; the margin is computed automatically.</summary>
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

        // ------------------------- Restore -------------------------

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

        /// <summary>Pipe / conduit fittings: they move with the segment.</summary>
        private static bool IsFitting(Element e)
        {
            FamilyInstance fi = e as FamilyInstance;
            if (fi == null || e.Category == null) return false;

            bool inCategory = false;
            foreach (BuiltInCategory bic in Cfg.FittingCategories)
                if (e.Category.Id.Equals(new ElementId(bic))) { inCategory = true; break; }
            if (!inCategory) return false;

            // Every conduit / pipe fitting (elbows, tees, unions, boxes in those categories...) travels with the chain.
            // Only families of OTHER categories (devices, lighting, electrical fixtures, equipment) are boundaries.
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

        private static List<Connector> GetRefs(Connector c, Element self)
        {
            var list = new List<Connector>();
            if (!c.IsConnected) return list;
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
                case ConflictKind.NoSlack: return "Adjacent run would collapse";
                case ConflictKind.Collision: return "Geometric collision";
                case ConflictKind.RevitRejected: return "Revit rejected the adjustment";
                default: return "Geometry cannot be evaluated";
            }
        }
    }

    // ------------------------------------------------------------------------------
    //  Revit errors / warnings handling during the alignment
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
                    // Never auto-resolve: Revit's default fixes can disconnect fittings from the run.
                    // The attempt is rolled back and the next movement strategy is tried instead.
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
    //  Floating palette: switch axis (single segment only), placed next to the moved segment
    // ------------------------------------------------------------------------------
    internal enum PaletteAction { Toggle, Accept }

    internal static class AxisTogglePalette
    {
        private static AxisToggleWindow _window;
        private static ExternalEvent _extEvent;
        private static AxisToggleHandler _handler;
        private static AlignSession _session;
        private static PaletteKeyHook _hook;

        public static PaletteAction Pending = PaletteAction.Toggle;

        private static void DisposeHook()
        {
            PaletteKeyHook h = _hook;
            _hook = null;
            if (h != null)
            {
                try { h.Dispose(); } catch { }
            }
        }

        /// <summary>Closes the palette (if any), removes the keyboard hook and restores the temporary graphics.</summary>
        public static void CloseAndRestore(UIDocument uidoc)
        {
            DisposeHook();

            AxisToggleWindow w = _window;
            _window = null;
            if (w != null)
            {
                try { w.Close(); } catch { }
            }

            AlignSession s = _session;
            _session = null;
            if (s != null && uidoc != null)
            {
                try { AlignEngine.ClearVisuals(s, uidoc); } catch { }
            }
        }

        private static void Raise(PaletteAction action)
        {
            Pending = action;
            if (_extEvent != null) _extEvent.Raise();
        }

        private static Line LineOf(Element e)
        {
            LocationCurve lc = e != null ? e.Location as LocationCurve : null;
            return lc != null ? lc.Curve as Line : null;
        }

        private static bool Same(XYZ a, XYZ b)
        {
            return a != null && b != null && a.DistanceTo(b) < Cfg.AlignTolFt;
        }

        private static void Notice(string text)
        {
            TaskDialog.Show(Cfg.AppTitle, text);
        }

        /// <summary>Shows the palette only if there is ONE segment, it moved, and no boundary family moved.</summary>
        public static void ShowIfApplicable(UIDocument uidoc, AlignSession s)
        {
            CloseAndRestore(uidoc);
            if (uidoc == null || s == null) return;
            if (s.TargetIds.Count != 1 || s.FamilyMoved) return;
            if (s.OrigP0 == null || s.OrigP1 == null) return;

            Line now = LineOf(s.Document.GetElement(s.TargetIds[0]));
            if (now == null) return;
            s.AlignedP0 = now.GetEndPoint(0);
            s.AlignedP1 = now.GetEndPoint(1);

            // If nothing moved there is nothing to switch
            if (Same(s.OrigP0, s.AlignedP0) && Same(s.OrigP1, s.AlignedP1)) return;

            _session = s;
            if (_handler == null)
            {
                _handler = new AxisToggleHandler();
                _extEvent = ExternalEvent.Create(_handler);
            }

            // Focus: Halftone on everything except the moved chain / pivot while the user decides
            AlignEngine.ApplyFocusHalftone(s, uidoc);

            AxisToggleWindow w = new AxisToggleWindow();
            w.SetAxis(s.UsedAxis);
            w.RequestToggle = () =>
            {
                w.SetBusy(true);
                Raise(PaletteAction.Toggle);
            };
            w.RequestClose = () => Raise(PaletteAction.Accept);
            // Closed by other means (Alt+F4...): still restore the graphics
            w.Closed += (sender, args) =>
            {
                if (_window == w)
                {
                    _window = null;
                    DisposeHook();
                    Raise(PaletteAction.Accept);
                }
            };
            _window = w;

            try { new WindowInteropHelper(w).Owner = NativeWin.Hwnd(); }
            catch { }

            PositionWindow(uidoc, s, w);
            w.Show();

            // Enter / Space = accept current result (never toggles the axis)
            _hook = new PaletteKeyHook(() => Raise(PaletteAction.Accept));
        }

        /// <summary>
        /// Projects the midpoint of the moved segment to screen coordinates through UIView.GetZoomCorners()
        /// and places the palette slightly offset, clamped to the visible area of the Revit view.
        /// </summary>
        private static void PositionWindow(UIDocument uidoc, AlignSession s, AxisToggleWindow w)
        {
            double scale = NativeWin.Scale();
            double vl = 0, vt = 0, vr = 0, vb = 0;
            bool haveRect = false;
            double px = 0, py = 0;
            bool havePt = false;

            try
            {
                Document doc = s.Document;
                View view = doc.ActiveView;
                UIView uv = null;
                foreach (UIView u in uidoc.GetOpenUIViews())
                {
                    if (u.ViewId.Equals(view.Id)) { uv = u; break; }
                }

                if (uv != null)
                {
                    var rect = uv.GetWindowRectangle();
                    var tl = new NativeWin.POINT { X = rect.Left, Y = rect.Top };
                    var br = new NativeWin.POINT { X = rect.Right, Y = rect.Bottom };
                    if (!Cfg.ViewRectIsScreenSpace)
                    {
                        NativeWin.ClientToScreen(NativeWin.Hwnd(), ref tl);
                        NativeWin.ClientToScreen(NativeWin.Hwnd(), ref br);
                    }
                    vl = tl.X; vt = tl.Y; vr = br.X; vb = br.Y;
                    haveRect = vr > vl && vb > vt;

                    XYZ mid = AlignEngine.MidPoint(doc.GetElement(s.TargetIds[0]));
                    IList<XYZ> corners = uv.GetZoomCorners();
                    if (haveRect && mid != null && corners != null && corners.Count >= 2)
                    {
                        XYZ right = view.RightDirection;
                        XYZ up = view.UpDirection;
                        XYZ diag = corners[1].Subtract(corners[0]);
                        double wModel = diag.DotProduct(right);
                        double hModel = diag.DotProduct(up);
                        if (Math.Abs(wModel) > 1e-9 && Math.Abs(hModel) > 1e-9)
                        {
                            XYZ rel = mid.Subtract(corners[0]);
                            double fx = rel.DotProduct(right) / wModel;
                            double fy = rel.DotProduct(up) / hModel;
                            px = vl + fx * (vr - vl);
                            py = vt + (1.0 - fy) * (vb - vt);
                            havePt = true;
                        }
                    }
                }
            }
            catch { }

            if (!haveRect)
            {
                NativeWin.RECT mr;
                if (NativeWin.GetWindowRect(NativeWin.Hwnd(), out mr))
                {
                    vl = mr.Left; vt = mr.Top; vr = mr.Right; vb = mr.Bottom;
                    haveRect = vr > vl && vb > vt;
                }
            }
            if (!haveRect) return;   // keeps the default position only if no screen data is available at all

            if (!havePt)
            {
                px = (vl + vr) / 2.0;
                py = (vt + vb) / 2.0;
            }

            // Pixels -> device-independent units
            double minX = vl / scale, maxX = vr / scale;
            double minY = vt / scale, maxY = vb / scale;
            double cx = px / scale, cy = py / scale;

            double width = 150, height = 90;
            FrameworkElement fe = w.Content as FrameworkElement;
            if (fe != null)
            {
                fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                width = fe.DesiredSize.Width;
                height = fe.DesiredSize.Height;
            }

            const double offset = 28;
            double left = cx + offset;
            double top = cy + offset;
            if (left + width > maxX) left = cx - offset - width;    // flip to the other side instead of covering the element
            if (top + height > maxY) top = cy - offset - height;

            left = Math.Max(minX, Math.Min(left, maxX - width));
            top = Math.Max(minY, Math.Min(top, maxY - height));

            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = left;
            w.Top = top;
        }

        private static string SetCurve(Document doc, Element e, XYZ p0, XYZ p1)
        {
            var pre = new RollbackOnErrorPreprocessor();
            using (var t = new Transaction(doc, "Align segment (switch axis)"))
            {
                t.Start();
                try
                {
                    FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                    fho.SetFailuresPreprocessor(pre);
                    fho.SetClearAfterRollback(true);
                    t.SetFailureHandlingOptions(fho);

                    ((LocationCurve)e.Location).Curve = Line.CreateBound(p0, p1);

                    if (t.Commit() == TransactionStatus.Committed) return null;
                    return string.IsNullOrEmpty(pre.ErrorText) ? "Revit rejected the change." : pre.ErrorText;
                }
                catch (Exception ex)
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    return ex.Message;
                }
            }
        }

        /// <summary>Enter / Space / close: accepts the current position, restores graphics and closes the palette.</summary>
        public static void DoAccept(UIApplication app)
        {
            UIDocument uidoc = null;
            try { uidoc = app.ActiveUIDocument; } catch { }
            CloseAndRestore(uidoc);
        }

        /// <summary>Undoes the alignment (back to the original position) and aligns with the other axis.</summary>
        public static void DoToggle(UIApplication app)
        {
            AlignSession s = _session;
            AxisToggleWindow win = _window;
            PaletteKeyHook hook = _hook;
            if (hook != null) hook.Suspended = true;   // keys must reach any dialog shown below

            try
            {
                UIDocument uidoc = app.ActiveUIDocument;
                if (s == null || uidoc == null || !uidoc.Document.Equals(s.Document))
                {
                    Notice("The active document changed. The palette is no longer valid.");
                    CloseAndRestore(uidoc);
                    return;
                }

                Document doc = s.Document;
                Element curve = doc.GetElement(s.TargetIds[0]);
                Line now = LineOf(curve);
                if (now == null)
                {
                    Notice("The segment no longer exists or has changed. The palette is no longer valid.");
                    CloseAndRestore(uidoc);
                    return;
                }

                // If the user undid (Ctrl+Z) or moved the segment, the saved positions are no longer valid
                if (!Same(now.GetEndPoint(0), s.AlignedP0) || !Same(now.GetEndPoint(1), s.AlignedP1))
                {
                    Notice("The segment changed since the alignment. The palette is no longer valid.");
                    CloseAndRestore(uidoc);
                    return;
                }

                AlignAxis previous = s.UsedAxis;
                AlignAxis target = previous == AlignAxis.EqualizeY ? AlignAxis.EqualizeX : AlignAxis.EqualizeY;
                XYZ dirNew = target == AlignAxis.EqualizeY ? XYZ.BasisY : XYZ.BasisX;
                XYZ origDir = s.OrigP1.Subtract(s.OrigP0);
                if (origDir.GetLength() > 1e-9) origDir = origDir.Normalize();
                if (Math.Abs(origDir.DotProduct(dirNew)) > 0.999)
                {
                    Notice("The segment is parallel to the " + (target == AlignAxis.EqualizeY ? "Y" : "X") +
                           " axis: it cannot be aligned on that axis.");
                    return;
                }

                // Temporary focus graphics are removed while the model is being modified
                AlignEngine.ClearVisuals(s, uidoc);

                // 1) Back to the original position
                string err = SetCurve(doc, curve, s.OrigP0, s.OrigP1);
                if (err != null)
                {
                    Notice("The previous alignment could not be undone: " + err);
                    AlignEngine.ApplyFocusHalftone(s, uidoc);
                    return;
                }

                // 2) Align on the other axis with the same engine
                s.Axis = target;
                s.NextIndex = 0;
                s.Conflict = null;
                s.MovedIds.Clear();
                bool done = AlignEngine.ProcessPending(s, uidoc);

                if (done)
                {
                    Line after = LineOf(doc.GetElement(s.TargetIds[0]));
                    if (after != null)
                    {
                        s.AlignedP0 = after.GetEndPoint(0);
                        s.AlignedP1 = after.GetEndPoint(1);
                    }
                    if (win != null) win.SetAxis(s.UsedAxis);
                }
                else
                {
                    string why = s.Conflict != null ? s.Conflict.Message : "";
                    AlignEngine.ClearVisuals(s, uidoc);

                    // Restore the previous alignment
                    s.Axis = previous;
                    s.UsedAxis = previous;
                    s.NextIndex = 0;
                    s.Conflict = null;
                    SetCurve(doc, curve, s.AlignedP0, s.AlignedP1);
                    s.MovedIds.Clear();
                    s.MovedIds.Add(curve.Id);

                    Notice("The segment could not be aligned on the other axis. The previous alignment was kept." +
                           (string.IsNullOrEmpty(why) ? "" : "\n\n" + why));
                }

                // Focus graphics back on, and the palette follows the moved segment
                AlignEngine.ApplyFocusHalftone(s, uidoc);
                if (win != null) PositionWindow(uidoc, s, win);
            }
            catch (Exception ex)
            {
                Notice("Error while switching axis: " + ex.Message);
            }
            finally
            {
                if (win != null) win.SetBusy(false);
                if (hook != null) hook.Suspended = false;
            }
        }
    }

    internal sealed class AxisToggleHandler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            if (AxisTogglePalette.Pending == PaletteAction.Accept) AxisTogglePalette.DoAccept(app);
            else AxisTogglePalette.DoToggle(app);
        }
        public string GetName() { return "PipeAligner.AxisToggleHandler"; }
    }

    internal sealed class AxisToggleWindow : Window
    {
        private readonly TextBlock _info = new TextBlock();
        private readonly Button _btn = new Button();
        private readonly Button _close = new Button();

        public Action RequestToggle;
        public Action RequestClose;

        private const string ButtonStyleXaml =
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "      xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
            "  <Setter Property='Background' Value='Transparent'/>" +
            "  <Setter Property='BorderThickness' Value='0'/>" +
            "  <Setter Property='Cursor' Value='Hand'/>" +
            "  <Setter Property='Template'>" +
            "    <Setter.Value>" +
            "      <ControlTemplate TargetType='Button'>" +
            "        <Border x:Name='bd' Background='{TemplateBinding Background}' CornerRadius='3'>" +
            "          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
            "        </Border>" +
            "        <ControlTemplate.Triggers>" +
            "          <Trigger Property='IsMouseOver' Value='True'>" +
            "            <Setter TargetName='bd' Property='Background' Value='#4C5A6B'/>" +
            "          </Trigger>" +
            "          <Trigger Property='IsPressed' Value='True'>" +
            "            <Setter TargetName='bd' Property='Background' Value='#2A323D'/>" +
            "          </Trigger>" +
            "          <Trigger Property='IsEnabled' Value='False'>" +
            "            <Setter Property='Opacity' Value='0.4'/>" +
            "          </Trigger>" +
            "        </ControlTemplate.Triggers>" +
            "      </ControlTemplate>" +
            "    </Setter.Value>" +
            "  </Setter>" +
            "</Style>";

        public AxisToggleWindow()
        {
            Title = "Alignment axis";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowInTaskbar = false;
            ShowActivated = false;          // do not steal focus from the Revit view
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 68, 83));
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 40;
            Top = 140;

            System.Windows.Style st =
                (System.Windows.Style)System.Windows.Markup.XamlReader.Parse(ButtonStyleXaml);
            Resources.Add(typeof(Button), st);

            // Single button: curved arrow. Only a mouse CLICK triggers it: Enter / Space never do.
            _btn.Width = 40;
            _btn.Height = 40;
            _btn.Margin = new Thickness(4);
            _btn.IsDefault = false;
            _btn.IsCancel = false;
            _btn.Focusable = false;
            _btn.IsTabStop = false;
            _btn.Content = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse("M 4,24 C 4,9 20,3 26,16 M 26.8,9 L 26,16 L 19.7,12.9"),
                Stroke = System.Windows.Media.Brushes.White,
                StrokeThickness = 2.4,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                Stretch = System.Windows.Media.Stretch.Uniform,
                Width = 22,
                Height = 22
            };
            _btn.Click += (s, e) => { if (RequestToggle != null) RequestToggle(); };

            // Draggable header with the current axis and a small "x"
            _info.FontSize = 11;
            _info.FontWeight = FontWeights.SemiBold;
            _info.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(140, 230, 170));
            _info.VerticalAlignment = VerticalAlignment.Center;
            _info.Margin = new Thickness(0, 0, 10, 0);

            _close.Content = new TextBlock
            {
                Text = "×",
                FontSize = 14,
                Foreground = System.Windows.Media.Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };
            _close.Width = 18;
            _close.Height = 18;
            _close.IsDefault = false;
            _close.IsCancel = false;
            _close.Focusable = false;
            _close.IsTabStop = false;
            _close.ToolTip = "Accept and close";
            _close.Click += (s, e) => { if (RequestClose != null) RequestClose(); };

            var headerRow = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_close, Dock.Right);
            headerRow.Children.Add(_close);
            headerRow.Children.Add(_info);

            var header = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 56, 69)),
                Padding = new Thickness(8, 2, 4, 2),
                Child = headerRow
            };
            header.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };

            var hint = new TextBlock
            {
                Text = "Enter / Space = accept",
                FontSize = 10,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(170, 180, 195)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 4)
            };

            var root = new StackPanel();
            root.Children.Add(header);
            root.Children.Add(_btn);
            root.Children.Add(hint);

            Content = new Border
            {
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(90, 102, 122)),
                BorderThickness = new Thickness(1),
                Child = root
            };
        }

        /// <summary>Shows the axis that was equalized and which one the button will switch to.</summary>
        public void SetAxis(AlignAxis used)
        {
            bool y = used == AlignAxis.EqualizeY;
            _info.Text = y ? "Equalize Y" : "Equalize X";
            _btn.ToolTip = y ? "Switch to X alignment" : "Switch to Y alignment";
        }

        public void SetBusy(bool busy)
        {
            _btn.IsEnabled = !busy;
        }
    }
}
