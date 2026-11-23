// =====================================================================================
//  ConectarVerticalCommand.cs   (Script 4, INDEPENDENT)
//
//  Stretches a pipe or conduit vertically (Z) to the elevation of a family connector
//  and CONNECTS it (ConnectTo), matching diameter when different.
//  Designed for when the segment and connector already align in X, Y.
//  Self-contained file: own namespace (PipeAlignerVertical), no dependency on other scripts.
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
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.ExternalService;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace PipeAlignerVertical
{
    // ------------------------------------------------------------------------------
    //  Configuration
    // ------------------------------------------------------------------------------
    internal static class Cfg
    {
        public const double MmToFt = 1.0 / 304.8;

        public const double XYTolMm = 5.0;
        public const double ZTolMm = 1.0;
        public const double MinDropMm = 100.0;
        public const double MinLengthMm = 1.0;
        public const double DiameterTolMm = 0.5;
        public const double VerticalCos = 0.999;
        public const double SweepStartShrinkMm = 5.0;

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
    }

    // ------------------------------------------------------------------------------
    //  Data models
    // ------------------------------------------------------------------------------
    internal enum VerticalMode
    {
        VerticalStretch,
        Drop,
        DirectConnect
    }

    internal enum DiameterChoice { None, AdaptTube, AdaptConnector, Cancel }

    internal sealed class VerticalPlan
    {
        public string Error;
        public MEPCurve Tube;
        public FamilyInstance Fam;
        public int FamConnId;
        public XYZ FamPoint;

        public VerticalMode Mode;
        public int EndIndex;
        public XYZ OldEnd;
        public XYZ FixedEnd;
        public XYZ NewEnd;
        public XYZ DropEnd;

        public List<Connector> EndRefs = new List<Connector>();
        public bool DisconnectEnd;
        public bool DisconnectFamily;
    }

    internal sealed class ParamTarget
    {
        public Parameter Param;
        public string Name;
        public bool IsType;
        public bool ReadOnly;
        public bool IsRadius;
        public string TypeName;
    }

    // ------------------------------------------------------------------------------
    //  Command
    // ------------------------------------------------------------------------------
    [Transaction(TransactionMode.Manual)]
    public class ConectarVerticalCommand : IExternalCommand
    {
        private const string Caption = "Stretch Vertical & Connect";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            FamilyInstance fam = null;
            Connector conn = null;

            try
            {
                Reference fr = uidoc.Selection.PickObject(ObjectType.Element, new ConnectorFamilyFilter(),
                    "Select the FAMILY that has the target connector");
                fam = doc.GetElement(fr.ElementId) as FamilyInstance;

                List<Connector> conns = ListConnectors(fam);
                if (conns.Count == 0)
                {
                    TaskDialog.Show(Caption, "The family has no usable connectors.");
                    return Result.Cancelled;
                }

                if (conns.Count == 1) conn = conns[0];
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

            return ExecuteWithConnector(uidoc, fam, conn, ref message);
        }

        internal static Result ExecuteWithConnector(UIDocument uidoc, FamilyInstance fam, Connector conn, ref string message)
        {
            Document doc = uidoc.Document;
            Reference tubeRef;
            try
            {
                tubeRef = uidoc.Selection.PickObject(ObjectType.Element, new MepCurveSelectionFilter(),
                    "Select the pipe or conduit to stretch vertically to the connector");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            MEPCurve tube = doc.GetElement(tubeRef.ElementId) as MEPCurve;

            VerticalPlan plan = VerticalEngine.Plan(doc, fam, conn, tube);
            if (plan.Error != null)
            {
                TaskDialog.Show(Caption, plan.Error);
                return Result.Cancelled;
            }

            if (Math.Abs(conn.CoordinateSystem.BasisZ.Z) < 0.7)
            {
                var tdDir = new TaskDialog(Caption);
                tdDir.MainInstruction = "The connector does not point vertically";
                tdDir.MainContent = "The family connector points horizontally; the vertical segment will arrive from the side. Continue?";
                tdDir.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                if (tdDir.Show() != TaskDialogResult.Yes) return Result.Cancelled;
            }

            if (plan.EndRefs.Count > 0)
            {
                Element owner = plan.EndRefs[0].Owner;
                ElementId ownerId = owner != null ? owner.Id : ElementId.InvalidElementId;
                var td = new TaskDialog(Caption);
                td.MainInstruction = "The segment end is connected";
                td.MainContent = "It is connected to: " + VerticalEngine.Describe(doc, ownerId) +
                                 "\n\nContinuing will disconnect that end (the other element stays in place).";
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Disconnect and continue");
                td.CommonButtons = TaskDialogCommonButtons.Cancel;
                if (td.Show() != TaskDialogResult.CommandLink1) return Result.Cancelled;
                plan.DisconnectEnd = true;
            }

            List<Connector> famRefs = VerticalEngine.GetRefs(conn, fam)
                .Where(r => r.Owner != null && !r.Owner.Id.Equals(tube.Id)).ToList();
            if (famRefs.Count > 0)
            {
                var td = new TaskDialog(Caption);
                td.MainInstruction = "The family connector is already connected";
                td.MainContent = "It is connected to: " + VerticalEngine.Describe(doc, famRefs[0].Owner.Id) +
                                 "\n\nContinuing will disconnect it from that element.";
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Disconnect and continue");
                td.CommonButtons = TaskDialogCommonButtons.Cancel;
                if (td.Show() != TaskDialogResult.CommandLink1) return Result.Cancelled;
                plan.DisconnectFamily = true;
            }

            XYZ hitPoint;
            Element obstacle = VerticalEngine.FindObstacle(doc, plan, conn, out hitPoint);
            if (obstacle != null)
            {
                try
                {
                    uidoc.Selection.SetElementIds(new List<ElementId> { obstacle.Id });
                    uidoc.ShowElements(obstacle.Id);
                }
                catch { }

                var td = new TaskDialog(Caption);
                td.MainInstruction = "An element is in the path";
                td.MainContent = "The new vertical segment crosses: " + VerticalEngine.Describe(doc, obstacle.Id) +
                                 " (selected).\n\nContinue anyway?";
                td.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                if (td.Show() != TaskDialogResult.Yes) return Result.Cancelled;
            }

            double connDia = VerticalEngine.ConnectorDiameter(conn);
            double tubeDia = VerticalEngine.TubeDiameter(tube);
            DiameterChoice choice = DiameterChoice.None;
            ParamTarget target = null;

            if (connDia > 0 && tubeDia > 0 && Math.Abs(connDia - tubeDia) > Cfg.DiameterTolMm * Cfg.MmToFt)
            {
                while (true)
                {
                    var dlg = new DiameterDialog(VerticalEngine.IsPipe(tube) ? "pipe" : "conduit",
                        tubeDia / Cfg.MmToFt, connDia / Cfg.MmToFt);
                    try { new WindowInteropHelper(dlg).Owner = Process.GetCurrentProcess().MainWindowHandle; }
                    catch { }
                    dlg.ShowDialog();

                    if (dlg.Choice == DiameterChoice.AdaptTube) { choice = DiameterChoice.AdaptTube; break; }
                    if (dlg.Choice != DiameterChoice.AdaptConnector) return Result.Cancelled;

                    string why;
                    ParamTarget cand = VerticalEngine.FindConnectorParam(fam, conn, out why);
                    if (cand == null)
                    {
                        TaskDialog.Show(Caption, why + "\n\nChoose another option.");
                        continue;
                    }
                    if (cand.ReadOnly)
                    {
                        TaskDialog.Show(Caption,
                            "The parameter '" + cand.Name + "' associated with the connector is READ-ONLY and cannot be modified.\n\nChoose another option.");
                        continue;
                    }
                    if (cand.IsType)
                    {
                        var td = new TaskDialog(Caption);
                        td.MainInstruction = "The parameter is a TYPE parameter";
                        td.MainContent = "The parameter '" + cand.Name + "' belongs to type '" + cand.TypeName +
                                         "': changing it will modify ALL instances of that type.\n\nApply anyway?";
                        td.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                        if (td.Show() != TaskDialogResult.Yes) continue;
                    }
                    choice = DiameterChoice.AdaptConnector;
                    target = cand;
                    break;
                }
            }

            string err = VerticalEngine.Apply(doc, plan, choice, target, connDia, tubeDia);
            if (err != null)
            {
                TaskDialog.Show(Caption, "Could not complete the operation; no changes were made.\n\n" + err);
                return Result.Cancelled;
            }

            try
            {
                AlignElementsSuite.ConnectorPalette.ShowFor(uidoc, fam.Id, conn.Id);
            }
            catch { }

            return Result.Succeeded;
        }

        internal static bool IsUsableDomain(Connector c)
        {
            Domain d = c.Domain;
            return d == Domain.DomainPiping ||
                   d == Domain.DomainElectrical ||
                   d == Domain.DomainCableTrayConduit;
        }

        private static List<Connector> ListConnectors(FamilyInstance fam)
        {
            return VerticalEngine.GetConnectors(fam).Where(IsUsableDomain).OrderBy(c => c.Id).ToList();
        }

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
            KeyConfirmHook keyHook = null;

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
                            SketchPlane sp = SketchPlane.Create(doc,
                                Plane.CreateByNormalAndOrigin(XYZ.BasisZ, planeOrigin));
                            view.SketchPlane = sp;
                        }
                        catch { }

                        try
                        {
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
                    keyHook = new KeyConfirmHook();
                    uidoc.RefreshActiveView();

                    while (true)
                    {
                        keyHook.Confirmed = false;
                        keyHook.Enabled = server.Selected >= 0;

                        try
                        {
                            XYZ click = uidoc.Selection.PickPoint(ObjectSnapTypes.None,
                                "Click near connector  ·  Enter / Space = confirm  ·  Esc = cancel");

                            server.Selected = NearestByRay(view, click, conns);
                            uidoc.RefreshActiveView();
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                        {
                            if (keyHook.Confirmed && server.Selected >= 0) chosen = conns[server.Selected];
                            break;
                        }
                    }
                }
                finally
                {
                    if (keyHook != null) keyHook.Dispose();
                    server.Unregister();
                    if (tg.GetStatus() == TransactionStatus.Started) tg.RollBack();
                    try { uidoc.RefreshActiveView(); } catch { }
                }
            }

            return chosen;
        }

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
    }

    internal sealed class ConnectorFamilyFilter : ISelectionFilter
    {
        public bool AllowElement(Element e)
        {
            FamilyInstance fi = e as FamilyInstance;
            if (fi == null || fi.MEPModel == null) return false;

            foreach (Connector c in VerticalEngine.GetConnectors(fi))
                if (ConectarVerticalCommand.IsUsableDomain(c)) return true;
            return false;
        }
        public bool AllowReference(Reference r, XYZ p) { return false; }
    }

    internal sealed class KeyConfirmHook : IDisposable
    {
        private const int WH_KEYBOARD = 2;
        private const int VK_RETURN = 0x0D;
        private const int VK_SPACE = 0x20;
        private const int VK_ESCAPE = 0x1B;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private readonly HookProc _proc;
        private IntPtr _hook = IntPtr.Zero;

        public volatile bool Enabled;
        public volatile bool Confirmed;

        public KeyConfirmHook()
        {
            _proc = Proc;
            _hook = SetWindowsHookEx(WH_KEYBOARD, _proc, IntPtr.Zero, GetCurrentThreadId());
        }

        private IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && Enabled)
            {
                int vk = wParam.ToInt32();
                long l = lParam.ToInt64();
                bool keyUp = (l & 0x80000000L) != 0;
                bool repeat = (l & 0x40000000L) != 0;

                if (vk == VK_RETURN || vk == VK_SPACE)
                {
                    if (!keyUp && !repeat && !Confirmed)
                    {
                        Confirmed = true;
                        keybd_event((byte)VK_ESCAPE, 0, 0, UIntPtr.Zero);
                        keybd_event((byte)VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    }
                    return (IntPtr)1;
                }
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }

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

            var cyan = new ColorWithTransparency(34, 184, 219, 0);
            var gray = new ColorWithTransparency(145, 145, 145, 0);
            var picked = new ColorWithTransparency(0, 210, 90, 0);

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

                add(o.Subtract(XYZ.BasisX.Multiply(r)), o.Add(XYZ.BasisX.Multiply(r)), col);
                add(o.Subtract(XYZ.BasisY.Multiply(r)), o.Add(XYZ.BasisY.Multiply(r)), col);
                add(o.Subtract(XYZ.BasisZ.Multiply(r)), o.Add(XYZ.BasisZ.Multiply(r)), col);

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
            double pad = 3.0;
            double x0 = _origins.Min(p => p.X) - pad, y0 = _origins.Min(p => p.Y) - pad, z0 = _origins.Min(p => p.Z) - pad;
            double x1 = _origins.Max(p => p.X) + pad, y1 = _origins.Max(p => p.Y) + pad, z1 = _origins.Max(p => p.Z) + pad;
            return new Outline(new XYZ(x0, y0, z0), new XYZ(x1, y1, z1));
        }

        public Guid GetServerId() { return _id; }
        public ExternalServiceId GetServiceId() { return ExternalServices.BuiltInExternalServices.DirectContext3DService; }
        public string GetName() { return "PipeAligner connector markers"; }
        public string GetVendorId() { return "PipeAligner"; }
        public string GetDescription() { return "Connector markers."; }
        public string GetApplicationId() { return ""; }
        public string GetSourceId() { return ""; }
        public bool UsesHandles() { return false; }
    }

    internal sealed class MepCurveSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element e)
        {
            if (VerticalEngine.IsPipe(e)) return true;
            if (VerticalEngine.IsConduit(e)) return true;
            return false;
        }
        public bool AllowReference(Reference r, XYZ p) { return false; }
    }

    internal sealed class DiameterDialog : Window
    {
        public DiameterChoice Choice = DiameterChoice.Cancel;

        public DiameterDialog(string tubeKind, double tubeMm, double connMm)
        {
            Title = "Mismatching Diameters";
            Width = 440;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;

            var panel = new StackPanel { Margin = new Thickness(14) };

            var title = new TextBlock
            {
                Text = "Diameter mismatch",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var detail = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                Text = "Family connector: Ø " + connMm.ToString("0.##") + " mm\n" +
                       tubeKind.Substring(0, 1).ToUpperInvariant() + tubeKind.Substring(1) + " diameter: Ø " + tubeMm.ToString("0.##") + " mm\n\n" +
                       "How would you like to match them to connect?"
            };

            var bTube = new Button { Content = "Match " + tubeKind + " to connector" };
            var bConn = new Button { Content = "Match family connector to " + tubeKind };
            var bCancel = new Button { Content = "Cancel" };
            foreach (Button b in new[] { bTube, bConn, bCancel })
            {
                b.Margin = new Thickness(0, 0, 0, 6);
                b.Padding = new Thickness(6, 5, 6, 5);
            }

            bTube.Click += (s, e) => { Choice = DiameterChoice.AdaptTube; DialogResult = true; };
            bConn.Click += (s, e) => { Choice = DiameterChoice.AdaptConnector; DialogResult = true; };
            bCancel.Click += (s, e) => { Choice = DiameterChoice.Cancel; DialogResult = false; };

            panel.Children.Add(title);
            panel.Children.Add(detail);
            panel.Children.Add(bTube);
            panel.Children.Add(bConn);
            panel.Children.Add(bCancel);
            Content = panel;
        }
    }

    internal static class VerticalEngine
    {
        public static bool IsPipe(Element e) { return e is Pipe; }

        public static bool IsConduit(Element e) { return e is Conduit; }

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

        private static BuiltInParameter DiameterParam(MEPCurve c)
        {
            return IsPipe(c) ? BuiltInParameter.RBS_PIPE_DIAMETER_PARAM : BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM;
        }

        public static double TubeDiameter(MEPCurve c)
        {
            Parameter p = c.get_Parameter(DiameterParam(c));
            return p != null && p.HasValue ? p.AsDouble() : -1;
        }

        public static double ConnectorDiameter(Connector c)
        {
            try
            {
                if (c.Shape == ConnectorProfileType.Round) return 2.0 * c.Radius;
            }
            catch { }
            return -1;
        }

        public static Line GetLine(MEPCurve p)
        {
            if (p == null) return null;
            LocationCurve lc = p.Location as LocationCurve;
            return lc != null ? lc.Curve as Line : null;
        }

        private static double Dxy(XYZ a, XYZ b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
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

        public static List<Connector> GetRefs(Connector c, Element self)
        {
            var list = new List<Connector>();
            foreach (Connector other in c.AllRefs)
            {
                if (other.Owner == null) continue;
                if (other.Owner.Id.Equals(self.Id)) continue;
                if (other.Owner is MEPSystem) continue;
                list.Add(other);
            }
            return list;
        }

        private static Connector ClosestConnector(MEPCurve p, XYZ point)
        {
            Connector best = null;
            double bestDist = double.MaxValue;
            foreach (Connector c in GetConnectors(p))
            {
                double d = c.Origin.DistanceTo(point);
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return best;
        }

        private static Connector FindFamilyConnector(FamilyInstance fam, int id, XYZ point)
        {
            List<Connector> list = GetConnectors(fam);
            foreach (Connector c in list)
                if (c.Id == id) return c;

            Connector best = null;
            double bestDist = double.MaxValue;
            foreach (Connector c in list)
            {
                double d = c.Origin.DistanceTo(point);
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return best;
        }

        public static VerticalPlan Plan(Document doc, FamilyInstance fam, Connector conn, MEPCurve tube)
        {
            var p = new VerticalPlan { Tube = tube, Fam = fam, FamConnId = conn.Id, FamPoint = conn.Origin };

            Line line = GetLine(tube);
            if (line == null)
            {
                p.Error = "The segment is not straight: only linear segments can be stretched.";
                return p;
            }

            Domain want = IsPipe(tube) ? Domain.DomainPiping : Domain.DomainCableTrayConduit;
            if (conn.Domain != want)
            {
                p.Error = "The connector has domain '" + conn.Domain + "' and is not compatible with " +
                          (IsPipe(tube) ? "a pipe" : "a conduit") + ".";
                return p;
            }

            XYZ cp = conn.Origin;
            XYZ e0 = line.GetEndPoint(0), e1 = line.GetEndPoint(1);
            double xyTol = Cfg.XYTolMm * Cfg.MmToFt;
            double zTol = Cfg.ZTolMm * Cfg.MmToFt;
            string xyMsg = "The segment and connector do not align in X, Y (difference of {0} mm; tolerance {1} mm).\n\n" +
                           "This command assumes they already align: align them in plan view first.";

            int idx;
            if (Math.Abs(line.Direction.Z) > Cfg.VerticalCos)
            {
                double dxy = Dxy(e0, cp);
                if (dxy > xyTol)
                {
                    p.Error = string.Format(xyMsg, (dxy / Cfg.MmToFt).ToString("0.#"), Cfg.XYTolMm.ToString("0.#"));
                    return p;
                }

                idx = e0.DistanceTo(cp) <= e1.DistanceTo(cp) ? 0 : 1;
                p.Mode = VerticalMode.VerticalStretch;
                p.EndIndex = idx;
                p.OldEnd = idx == 0 ? e0 : e1;
                p.FixedEnd = idx == 0 ? e1 : e0;
                p.NewEnd = new XYZ(p.OldEnd.X, p.OldEnd.Y, cp.Z);

                XYZ u = p.OldEnd.Subtract(p.FixedEnd);
                XYZ v2 = p.NewEnd.Subtract(p.FixedEnd);
                if (v2.DotProduct(u) <= 0 || v2.GetLength() < Cfg.MinLengthMm * Cfg.MmToFt)
                {
                    p.Error = "The connector elevation is past (or too close to) the fixed end of the segment: " +
                              "the segment would invert or become shorter than " + Cfg.MinLengthMm.ToString("0") + " mm.";
                    return p;
                }
            }
            else
            {
                double d0 = Dxy(e0, cp), d1 = Dxy(e1, cp);
                idx = d0 <= d1 ? 0 : 1;
                double dxy = Math.Min(d0, d1);
                if (dxy > xyTol)
                {
                    p.Error = string.Format(xyMsg, (dxy / Cfg.MmToFt).ToString("0.#"), Cfg.XYTolMm.ToString("0.#"));
                    return p;
                }

                p.EndIndex = idx;
                p.OldEnd = idx == 0 ? e0 : e1;
                p.FixedEnd = idx == 0 ? e1 : e0;

                double dz = cp.Z - p.OldEnd.Z;
                if (Math.Abs(dz) <= zTol)
                {
                    p.Mode = VerticalMode.DirectConnect;
                }
                else if (Math.Abs(dz) < Cfg.MinDropMm * Cfg.MmToFt)
                {
                    p.Error = "The elevation difference (" + (Math.Abs(dz) / Cfg.MmToFt).ToString("0.#") +
                              " mm) is too small to create an elbow and vertical drop (minimum " +
                              Cfg.MinDropMm.ToString("0") + " mm).";
                    return p;
                }
                else
                {
                    p.Mode = VerticalMode.Drop;
                    p.DropEnd = new XYZ(p.OldEnd.X, p.OldEnd.Y, cp.Z);
                }
            }

            Connector endConn = ClosestConnector(tube, p.OldEnd);
            if (endConn != null) p.EndRefs = GetRefs(endConn, tube);
            return p;
        }

        public static Element FindObstacle(Document doc, VerticalPlan p, Connector conn, out XYZ point)
        {
            point = null;

            XYZ a, b;
            if (p.Mode == VerticalMode.VerticalStretch)
            {
                a = p.OldEnd; b = p.NewEnd;
                if (b.Subtract(a).DotProduct(a.Subtract(p.FixedEnd)) <= 0) return null;
            }
            else if (p.Mode == VerticalMode.Drop)
            {
                a = p.OldEnd; b = p.DropEnd;
            }
            else return null;

            XYZ move = b.Subtract(a);
            double dl = move.GetLength();
            if (dl < 1e-6) return null;

            double radius = GetRadius(p.Tube);
            try { if (conn.Shape == ConnectorProfileType.Round) radius = Math.Max(radius, conn.Radius); } catch { }

            XYZ u2 = move.Normalize();
            double shrink = Cfg.SweepStartShrinkMm * Cfg.MmToFt;
            double length = dl - shrink;
            if (length <= 0) return null;
            XYZ start = a.Add(u2.Multiply(shrink));

            XYZ n1 = u2.CrossProduct(XYZ.BasisZ);
            if (n1.GetLength() < 1e-6) n1 = u2.CrossProduct(XYZ.BasisX);
            n1 = n1.Normalize();
            XYZ n2 = u2.CrossProduct(n1).Normalize();

            XYZ c1 = start.Subtract(n1.Multiply(radius)).Subtract(n2.Multiply(radius));
            XYZ c2 = start.Add(n1.Multiply(radius)).Subtract(n2.Multiply(radius));
            XYZ c3 = start.Add(n1.Multiply(radius)).Add(n2.Multiply(radius));
            XYZ c4 = start.Subtract(n1.Multiply(radius)).Add(n2.Multiply(radius));

            Solid sweep;
            try
            {
                var loop = new CurveLoop();
                loop.Append(Line.CreateBound(c1, c2));
                loop.Append(Line.CreateBound(c2, c3));
                loop.Append(Line.CreateBound(c3, c4));
                loop.Append(Line.CreateBound(c4, c1));
                sweep = GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { loop }, u2, length);
            }
            catch
            {
                return null;
            }

            XYZ[] corners = { c1, c2, c3, c4 };
            XYZ shift = u2.Multiply(length);
            var all = new List<XYZ>(corners);
            foreach (XYZ k in corners) all.Add(k.Add(shift));
            double pad = Cfg.MmToFt;
            XYZ sMin = new XYZ(all.Min(q => q.X) - pad, all.Min(q => q.Y) - pad, all.Min(q => q.Z) - pad);
            XYZ sMax = new XYZ(all.Max(q => q.X) + pad, all.Max(q => q.Y) + pad, all.Max(q => q.Z) + pad);

            var exclude = new HashSet<ElementId> { p.Tube.Id, p.Fam.Id };
            foreach (Connector rc in p.EndRefs) if (rc.Owner != null) exclude.Add(rc.Owner.Id);

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

            Element best = null;
            double bestDist = double.MaxValue;
            foreach (ElementId id in hits)
            {
                if (exclude.Contains(id)) continue;
                Element el = doc.GetElement(id);
                if (el == null) continue;
                BoundingBoxXYZ bb = el.get_BoundingBox(null);
                XYZ pt = bb != null ? bb.Min.Add(bb.Max).Multiply(0.5) : start;
                double dist = pt.DistanceTo(start);
                if (dist < bestDist) { bestDist = dist; best = el; point = pt; }
            }
            return best;
        }

        private static ElementId AssociatedParam(object info, BuiltInParameter bip)
        {
            try
            {
                MethodInfo m = info.GetType().GetMethod("GetAssociateFamilyParameterId", new[] { typeof(ElementId) });
                if (m == null) return ElementId.InvalidElementId;
                ElementId id = m.Invoke(info, new object[] { new ElementId(bip) }) as ElementId;
                return id ?? ElementId.InvalidElementId;
            }
            catch { return ElementId.InvalidElementId; }
        }

        public static ParamTarget FindConnectorParam(FamilyInstance fam, Connector conn, out string why)
        {
            why = null;
            double rad = conn.Radius, dia = 2.0 * rad;
            const double tol = 1e-4;

            ElementId assocR = ElementId.InvalidElementId, assocD = ElementId.InvalidElementId;
            try
            {
                MEPFamilyConnectorInfo info = conn.GetMEPConnectorInfo() as MEPFamilyConnectorInfo;
                if (info != null)
                {
                    assocR = AssociatedParam(info, BuiltInParameter.CONNECTOR_RADIUS);
                    assocD = AssociatedParam(info, BuiltInParameter.CONNECTOR_DIAMETER);
                }
            }
            catch { }

            var cands = new List<KeyValuePair<int, ParamTarget>>();

            Action<Parameter, bool> consider = (prm, isType) =>
            {
                if (prm == null || prm.StorageType != StorageType.Double) return;

                string name = prm.Definition != null ? prm.Definition.Name : "";
                string n = name.ToLowerInvariant();
                bool nameMatch = n.Contains("radi") || n.Contains("diam") || n.Contains("diám") || n.Contains("nominal");

                bool offR = !assocR.Equals(ElementId.InvalidElementId) && prm.Id.Equals(assocR);
                bool offD = !assocD.Equals(ElementId.InvalidElementId) && prm.Id.Equals(assocD);
                bool official = offR || offD;
                if (!official && !nameMatch) return;

                double v = prm.AsDouble();
                bool matchR = Math.Abs(v - rad) < tol;
                bool matchD = Math.Abs(v - dia) < tol;

                bool isRadius;
                if (offR) isRadius = true;
                else if (offD) isRadius = false;
                else if (matchR && !matchD) isRadius = true;
                else if (matchD && !matchR) isRadius = false;
                else isRadius = n.Contains("radi");

                int score = (official ? 100 : 0) + ((matchR || matchD) ? 50 : 0) + (nameMatch ? 20 : 0) + (isType ? 0 : 10);

                cands.Add(new KeyValuePair<int, ParamTarget>(score, new ParamTarget
                {
                    Param = prm,
                    Name = name,
                    IsType = isType,
                    ReadOnly = prm.IsReadOnly,
                    IsRadius = isRadius,
                    TypeName = isType && fam.Symbol != null ? fam.Symbol.Name : ""
                }));
            };

            foreach (Parameter prm in fam.Parameters) consider(prm, false);
            if (fam.Symbol != null)
                foreach (Parameter prm in fam.Symbol.Parameters) consider(prm, true);

            if (cands.Count == 0)
            {
                why = "Could not find a family parameter associated with this connector's radius/diameter.";
                return null;
            }

            cands = cands.OrderByDescending(k => k.Key).ToList();
            bool tie = cands.Count > 1 && cands[0].Key == cands[1].Key && cands[0].Key < 100 &&
                       cands[0].Value.Name != cands[1].Value.Name;
            if (tie)
            {
                why = "Several matching parameters were found (" + string.Join(", ", cands.Take(3).Select(k => k.Value.Name)) + ") and I cannot identify which one controls this connector.";
                return null;
            }

            return cands[0].Value;
        }

        public static string Apply(Document doc, VerticalPlan p, DiameterChoice choice, ParamTarget target,
                                   double connDia, double tubeDia)
        {
            var pre = new RollbackOnErrorPreprocessor();

            using (var t = new Transaction(doc, "Stretch vertical & connect"))
            {
                t.Start();
                try
                {
                    FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                    fho.SetFailuresPreprocessor(pre);
                    fho.SetClearAfterRollback(true);
                    t.SetFailureHandlingOptions(fho);

                    if (choice == DiameterChoice.AdaptTube)
                    {
                        Parameter dp = p.Tube.get_Parameter(DiameterParam(p.Tube));
                        if (dp == null || dp.IsReadOnly)
                            throw new InvalidOperationException("The segment diameter cannot be modified.");
                        dp.Set(connDia);
                        doc.Regenerate();
                    }
                    else if (choice == DiameterChoice.AdaptConnector && target != null)
                    {
                        target.Param.Set(target.IsRadius ? tubeDia / 2.0 : tubeDia);
                        doc.Regenerate();
                    }

                    Connector famConn = FindFamilyConnector(p.Fam, p.FamConnId, p.FamPoint);
                    if (famConn == null) throw new InvalidOperationException("Family connector not found.");

                    if (p.DisconnectEnd)
                    {
                        Connector endConn = ClosestConnector(p.Tube, p.OldEnd);
                        if (endConn != null)
                            foreach (Connector other in GetRefs(endConn, p.Tube).ToList())
                                endConn.DisconnectFrom(other);
                    }
                    if (p.DisconnectFamily)
                    {
                        foreach (Connector other in GetRefs(famConn, p.Fam).Where(r => !r.Owner.Id.Equals(p.Tube.Id)).ToList())
                            famConn.DisconnectFrom(other);
                    }

                    Connector toConnect;
                    if (p.Mode == VerticalMode.VerticalStretch)
                    {
                        if (p.OldEnd.DistanceTo(p.NewEnd) > Cfg.ZTolMm * Cfg.MmToFt)
                        {
                            LocationCurve lc = (LocationCurve)p.Tube.Location;
                            Line old = (Line)lc.Curve;
                            XYZ a = p.EndIndex == 0 ? p.NewEnd : old.GetEndPoint(0);
                            XYZ b = p.EndIndex == 1 ? p.NewEnd : old.GetEndPoint(1);
                            lc.Curve = Line.CreateBound(a, b);
                            doc.Regenerate();
                        }
                        toConnect = ClosestConnector(p.Tube, p.NewEnd);
                    }
                    else if (p.Mode == VerticalMode.Drop)
                    {
                        Connector tubeEnd = ClosestConnector(p.Tube, p.OldEnd);
                        MEPCurve vert = CreateVertical(doc, p.Tube, p.OldEnd, p.DropEnd);
                        doc.Regenerate();

                        Connector vStart = ClosestConnector(vert, p.OldEnd);
                        if (tubeEnd == null || vStart == null)
                            throw new InvalidOperationException("Could not find connectors to create the elbow.");

                        doc.Create.NewElbowFitting(tubeEnd, vStart);
                        doc.Regenerate();

                        toConnect = ClosestConnector(vert, p.DropEnd);
                    }
                    else
                    {
                        toConnect = ClosestConnector(p.Tube, p.OldEnd);
                    }

                    if (toConnect == null)
                        throw new InvalidOperationException("Could not find the segment connector to connect.");
                    if (toConnect.Origin.DistanceTo(famConn.Origin) > 2.0 * Cfg.XYTolMm * Cfg.MmToFt)
                        throw new InvalidOperationException("Connectors are not close enough to connect.");

                    toConnect.ConnectTo(famConn);

                    TransactionStatus st = t.Commit();
                    if (st == TransactionStatus.Committed) return null;

                    return "Revit rejected the change" + (string.IsNullOrEmpty(pre.ErrorText) ? "." : ": " + pre.ErrorText);
                }
                catch (Exception ex)
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    return ex.Message + (string.IsNullOrEmpty(pre.ErrorText) ? "" : "\n" + pre.ErrorText);
                }
            }
        }

        private static MEPCurve CreateVertical(Document doc, MEPCurve tube, XYZ a, XYZ b)
        {
            ElementId levelId = GetLevelId(doc, tube, a.Z);
            MEPCurve v;

            if (IsPipe(tube))
                v = CreatePipe(doc, (Pipe)tube, a, b, levelId);
            else
                v = CreateConduit(doc, (Conduit)tube, a, b, levelId);

            BuiltInParameter bip = DiameterParam(tube);
            Parameter src = tube.get_Parameter(bip);
            Parameter dst = v.get_Parameter(bip);
            if (src != null && dst != null && !dst.IsReadOnly) dst.Set(src.AsDouble());

            return v;
        }

        private static MEPCurve CreatePipe(Document doc, Pipe pipe, XYZ a, XYZ b, ElementId levelId)
        {
            ElementId sysId = ElementId.InvalidElementId;
            Parameter sp = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
            if (sp != null) sysId = sp.AsElementId();
            if (sysId == null || sysId.Equals(ElementId.InvalidElementId) || doc.GetElement(sysId) == null)
                sysId = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).FirstElementId();

            return Pipe.Create(doc, sysId, pipe.GetTypeId(), levelId, a, b);
        }

        private static MEPCurve CreateConduit(Document doc, Conduit conduit, XYZ a, XYZ b, ElementId levelId)
        {
            return Conduit.Create(doc, conduit.GetTypeId(), a, b, levelId);
        }

        private static ElementId GetLevelId(Document doc, MEPCurve tube, double z)
        {
            Parameter lp = tube.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM);
            if (lp != null)
            {
                ElementId id = lp.AsElementId();
                if (id != null && !id.Equals(ElementId.InvalidElementId) && doc.GetElement(id) is Level) return id;
            }

            Level nearest = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => Math.Abs(l.Elevation - z)).FirstOrDefault();
            return nearest != null ? nearest.Id : ElementId.InvalidElementId;
        }

        public static string Describe(Document doc, ElementId id)
        {
            Element e = (id == null || id.Equals(ElementId.InvalidElementId)) ? null : doc.GetElement(id);
            if (e == null) return "—";
            string cat = e.Category != null ? e.Category.Name : "Element";
            return cat + " · " + e.Name + " [Id " + id + "]";
        }
    }

    internal sealed class RollbackOnErrorPreprocessor : IFailuresPreprocessor
    {
        public string ErrorText;

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
                }
            }
            return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}