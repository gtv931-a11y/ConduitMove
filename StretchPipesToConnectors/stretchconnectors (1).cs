// =====================================================================================
//  StretchConnectors.cs   (Script 3, INDEPENDENT)
//
//  Stretches the nearest end of ONE pipe or conduit to the chosen connector
//  of a family (projected onto the segment axis so it does not skew, and connected
//  automatically if coaxial).
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.ExternalService;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace StretchConnectors
{
    internal static class Cfg
    {
        public const double MmToFt = 1.0 / 304.8;
        public const double MinCurveLengthMm = 1.0;
        public const double ConnectTolMm = 2.0;
    }

    // ------------------------------------------------------------------------------
    //  Main Command
    // ------------------------------------------------------------------------------
    [Transaction(TransactionMode.Manual)]
    public class EstirarConectorCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            FamilyInstance fam;
            Connector conn;
            if (!PickFamilyAndConnector(uidoc, doc, out fam, out conn))
                return Result.Cancelled;

            return ExecuteWithConnector(uidoc, fam, conn, ref message);
        }

        internal static bool PickFamilyAndConnector(UIDocument uidoc, Document doc, out FamilyInstance fam, out Connector conn)
        {
            fam = null;
            conn = null;
            try
            {
                Reference fr = uidoc.Selection.PickObject(ObjectType.Element, new ConnectorFamilyFilter(),
                    "Select the FAMILY that has the target connector");
                fam = doc.GetElement(fr.ElementId) as FamilyInstance;
                if (fam == null) return false;

                List<Connector> conns = ListConnectors(fam);
                if (conns.Count == 0)
                {
                    TaskDialog.Show("Stretch to Connector (Horizontal)", "The family has no usable connectors.");
                    return false;
                }

                if (conns.Count == 1)
                    conn = conns[0];
                else
                {
                    conn = ChooseConnector(uidoc, doc, fam, conns);
                    if (conn == null) return false;
                }
                return true;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return false;
            }
        }

        internal static Result ExecuteWithConnector(UIDocument uidoc, FamilyInstance fam, Connector conn, ref string message)
        {
            Document doc = uidoc.Document;
            MEPCurve curve = null;
            try
            {
                Reference tr = uidoc.Selection.PickObject(ObjectType.Element,
                    new MepCurveSelectionFilter(fam.Id),
                    "Select the pipe or conduit to stretch to the connector");
                curve = doc.GetElement(tr.ElementId) as MEPCurve;
                if (curve == null) return Result.Cancelled;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            string error;
            if (!StretchToConnector(doc, curve, conn, out error))
            {
                if (!string.IsNullOrEmpty(error))
                    TaskDialog.Show("Stretch to Connector (Horizontal)", error);
                return Result.Cancelled;
            }

            try
            {
                AlignElementsSuite.ConnectorPalette.ShowFor(uidoc, fam.Id, conn.Id);
            }
            catch { }

            return Result.Succeeded;
        }

        private static bool StretchToConnector(Document doc, MEPCurve curve, Connector targetConn, out string error)
        {
            error = null;
            LocationCurve lc = curve.Location as LocationCurve;
            Line line = lc != null ? lc.Curve as Line : null;
            if (line == null)
            {
                error = "The selected segment does not have valid linear geometry.";
                return false;
            }

            XYZ p0 = line.GetEndPoint(0);
            XYZ p1 = line.GetEndPoint(1);
            XYZ targetPos = targetConn.Origin;

            int nearIdx = p0.DistanceTo(targetPos) <= p1.DistanceTo(targetPos) ? 0 : 1;
            XYZ pNear = nearIdx == 0 ? p0 : p1;
            XYZ pFar = nearIdx == 0 ? p1 : p0;

            XYZ dir = pNear.Subtract(pFar);
            double curLen = dir.GetLength();
            if (curLen < 1e-6)
            {
                error = "The current length of the segment is too small.";
                return false;
            }
            dir = dir.Divide(curLen);

            double newLen = targetPos.Subtract(pFar).DotProduct(dir);
            if (newLen < Cfg.MinCurveLengthMm * Cfg.MmToFt)
            {
                error = "The connector is behind the opposite end; the segment would become inverted or zero length.";
                return false;
            }

            XYZ pNew = pFar.Add(dir.Multiply(newLen));
            if (pNew.DistanceTo(pNear) < 0.05 * Cfg.MmToFt)
                return true;

            Connector nearConn = FindConnectorAt(curve, pNear);
            if (nearConn != null && nearConn.IsConnected)
            {
                error = "The nearest end of the pipe/conduit is already connected to another element.";
                return false;
            }

            using (var t = new Transaction(doc, "Stretch pipe/conduit to connector"))
            {
                t.Start();
                try
                {
                    Line newLine = nearIdx == 0 ? Line.CreateBound(pNew, pFar) : Line.CreateBound(pFar, pNew);
                    lc.Curve = newLine;
                    doc.Regenerate();

                    if (!targetConn.IsConnected && pNew.DistanceTo(targetPos) <= Cfg.ConnectTolMm * Cfg.MmToFt)
                    {
                        Connector updatedNear = FindConnectorAt(curve, pNew);
                        if (updatedNear != null && !updatedNear.IsConnected && updatedNear.Domain == targetConn.Domain)
                        {
                            try { updatedNear.ConnectTo(targetConn); }
                            catch { }
                        }
                    }

                    t.Commit();
                    return true;
                }
                catch (Exception ex)
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    error = "Could not stretch the segment: " + ex.Message;
                    return false;
                }
            }
        }

        private static Connector FindConnectorAt(MEPCurve curve, XYZ pt)
        {
            if (curve == null || curve.ConnectorManager == null) return null;
            Connector best = null;
            double bestD = double.MaxValue;
            foreach (Connector c in curve.ConnectorManager.Connectors)
            {
                double d = c.Origin.DistanceTo(pt);
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        internal static bool IsUsableDomain(Connector c)
        {
            Domain d = c.Domain;
            return d == Domain.DomainPiping ||
                   d == Domain.DomainElectrical ||
                   d == Domain.DomainCableTrayConduit;
        }

        internal static List<Connector> GetConnectors(FamilyInstance fi)
        {
            var list = new List<Connector>();
            if (fi == null || fi.MEPModel == null || fi.MEPModel.ConnectorManager == null) return list;
            foreach (Connector c in fi.MEPModel.ConnectorManager.Connectors) list.Add(c);
            return list;
        }

        private static List<Connector> ListConnectors(FamilyInstance fi)
        {
            return GetConnectors(fi)
                .Where(IsUsableDomain)
                .OrderBy(c => c.Id)
                .ToList();
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
                    hook = new KeyConfirmHook();
                    uidoc.RefreshActiveView();

                    while (true)
                    {
                        hook.Confirmed = false;
                        hook.Enabled = server.Selected >= 0;

                        try
                        {
                            XYZ click = uidoc.Selection.PickPoint(ObjectSnapTypes.None,
                                "Click near connector  ·  Enter / Space = confirm  ·  Esc = cancel");

                            server.Selected = NearestByRay(view, click, conns);
                            uidoc.RefreshActiveView();
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                        {
                            if (hook.Confirmed && server.Selected >= 0) chosen = conns[server.Selected];
                            break;
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

    internal sealed class KeyConfirmHook : IDisposable
    {
        private const int WH_KEYBOARD = 2;
        private const uint WM_KEYDOWN = 0x0100;
        private const int VK_RETURN = 0x0D;
        private const int VK_SPACE = 0x20;

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
                            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)0x1B, IntPtr.Zero); // VK_ESCAPE
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

    internal sealed class ConnectorFamilyFilter : ISelectionFilter
    {
        public bool AllowElement(Element e)
        {
            FamilyInstance fi = e as FamilyInstance;
            if (fi == null || fi.MEPModel == null) return false;

            foreach (Connector c in EstirarConectorCommand.GetConnectors(fi))
                if (EstirarConectorCommand.IsUsableDomain(c)) return true;
            return false;
        }
        public bool AllowReference(Reference r, XYZ p) { return false; }
    }

    internal sealed class MepCurveSelectionFilter : ISelectionFilter
    {
        private readonly ElementId _exclude;
        public MepCurveSelectionFilter(ElementId exclude) { _exclude = exclude; }

        public bool AllowElement(Element e)
        {
            if (e.Id.Equals(_exclude)) return false;
            return e is Pipe || e is Conduit;
        }
        public bool AllowReference(Reference r, XYZ p) { return false; }
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
        public string GetName() { return "StretchConnectors markers"; }
        public string GetVendorId() { return "PipeAligner"; }
        public string GetDescription() { return "Ghost markers for connectors."; }
        public string GetApplicationId() { return ""; }
        public string GetSourceId() { return ""; }
        public bool UsesHandles() { return false; }
    }
}