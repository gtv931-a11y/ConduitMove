// =====================================================================================
//  DuctCore.cs   (namespace DuctAligner)
//
//  Shared building blocks of the RIGID DUCT module. Fully independent from the pipe /
//  conduit tools (own namespace, own classes, own markers, own palette state).
//
//   - DuctCfg                : constants
//   - DuctProfile(s)         : profile SHAPE evaluation (Round / Rectangular / Oval) + dimensions
//   - DuctUtil               : HVAC connectors, physical-connector guard, chain classification
//   - DuctPick               : family / connector picking (ghost markers + Enter/Space)
//   - KeyConfirmHook, selection filters, DirectContext3D marker server, failure preprocessor
//
//  C# 7.3 compatible.
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace DuctAligner
{
    // ------------------------------------------------------------------------------
    //  Configuration
    // ------------------------------------------------------------------------------
    internal static class DuctCfg
    {
        public const string Caption = "Align Ducts";

        public const double MmToFt = 1.0 / 304.8;

        public const double MinNeighbourLengthMm = 1.0;
        public const double FocusMarginMinMm = 500.0;
        public const double FocusMarginMaxMm = 5000.0;
        public const double FocusMarginFactor = 0.4;
        public const double AlignTolFt = 0.1 * MmToFt;
        public const double SkewTolFt = 0.5 * MmToFt;
        public const double SweepEndShrinkMm = 5.0;
        public const double ConnectTolMm = 2.0;
        public const double MinCurveLengthMm = 1.0;

        /// <summary>Size difference (0.1 mm) below which two profile dimensions are considered equal.</summary>
        public const double SizeTolFt = 0.1 / 304.8;

        /// <summary>|Direction.Z| at or below this: duct is "horizontal" and is also moved in Z (connector workflows).</summary>
        public const double HorizontalMaxSlope = 0.2;

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
            BuiltInCategory.OST_FlexDuctCurves,
            BuiltInCategory.OST_DuctFitting,
            BuiltInCategory.OST_DuctAccessory,
            BuiltInCategory.OST_DuctTerminal,
            BuiltInCategory.OST_CableTray,
            BuiltInCategory.OST_CableTrayFitting,
            BuiltInCategory.OST_StructuralFraming,
            BuiltInCategory.OST_StructuralColumns,
            BuiltInCategory.OST_GenericModel
        };
    }

    // ------------------------------------------------------------------------------
    //  Profile shape evaluation (shape compatibility, NOT exact size matching)
    // ------------------------------------------------------------------------------
    internal enum DuctShape { Unknown, Round, Rectangular, Oval }

    internal sealed class DuctProfile
    {
        public DuctShape Shape = DuctShape.Unknown;
        /// <summary>Round: diameter. Rectangular / Oval: width.</summary>
        public double Width;
        /// <summary>Round: diameter. Rectangular / Oval: height.</summary>
        public double Height;

        public bool IsKnown { get { return Shape != DuctShape.Unknown; } }
    }

    internal static class DuctProfiles
    {
        public static DuctProfile FromConnector(Connector c)
        {
            var p = new DuctProfile();
            if (c == null) return p;
            try
            {
                switch (c.Shape)
                {
                    case ConnectorProfileType.Round:
                        p.Shape = DuctShape.Round;
                        p.Width = p.Height = c.Radius * 2.0;
                        break;
                    case ConnectorProfileType.Rectangular:
                        p.Shape = DuctShape.Rectangular;
                        p.Width = c.Width;
                        p.Height = c.Height;
                        break;
                    case ConnectorProfileType.Oval:
                        p.Shape = DuctShape.Oval;
                        p.Width = c.Width;
                        p.Height = c.Height;
                        break;
                }
            }
            catch { }
            return p;
        }

        /// <summary>Profile of a rigid duct: read from its physical connectors, parameters as fallback.</summary>
        public static DuctProfile FromDuct(MEPCurve duct)
        {
            if (duct == null) return new DuctProfile();

            foreach (Connector c in DuctUtil.GetConnectors(duct))
            {
                if (!DuctUtil.IsPhysical(c)) continue;
                DuctProfile p = FromConnector(c);
                if (p.IsKnown) return p;
            }

            var q = new DuctProfile();
            try
            {
                Parameter dia = duct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                Parameter w = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                Parameter h = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                if (dia != null && dia.HasValue && dia.AsDouble() > 1e-9)
                {
                    q.Shape = DuctShape.Round;
                    q.Width = q.Height = dia.AsDouble();
                }
                else if (w != null && h != null && w.HasValue && h.HasValue)
                {
                    q.Shape = DuctShape.Rectangular;     // oval cannot be told apart from parameters alone
                    q.Width = w.AsDouble();
                    q.Height = h.AsDouble();
                }
            }
            catch { }
            return q;
        }

        /// <summary>Same profile SHAPE (a square is a Rectangular profile). Dimensions are irrelevant here.</summary>
        public static bool ShapeCompatible(DuctProfile a, DuctProfile b)
        {
            return a != null && b != null && a.IsKnown && b.IsKnown && a.Shape == b.Shape;
        }

        public static bool Compatible(Connector a, Connector b)
        {
            return ShapeCompatible(FromConnector(a), FromConnector(b));
        }

        public static bool SameSize(DuctProfile a, DuctProfile b)
        {
            if (!ShapeCompatible(a, b)) return false;
            if (a.Shape == DuctShape.Round) return Math.Abs(a.Width - b.Width) <= DuctCfg.SizeTolFt;
            return Math.Abs(a.Width - b.Width) <= DuctCfg.SizeTolFt && Math.Abs(a.Height - b.Height) <= DuctCfg.SizeTolFt;
        }

        private static string Mm(double ft)
        {
            return (ft * 304.8).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        public static string ShapeName(DuctShape s)
        {
            switch (s)
            {
                case DuctShape.Round: return "Round";
                case DuctShape.Rectangular: return "Rectangular";
                case DuctShape.Oval: return "Oval";
                default: return "Unknown";
            }
        }

        public static string Describe(DuctProfile p)
        {
            if (p == null || !p.IsKnown) return "Unknown profile";
            if (p.Shape == DuctShape.Round) return "Round Ø " + Mm(p.Width) + " mm";
            return ShapeName(p.Shape) + " " + Mm(p.Width) + " × " + Mm(p.Height) + " mm";
        }

        public static string MismatchMessage(DuctProfile a, DuctProfile b)
        {
            return "Profile incompatibility: " + Describe(a) + " cannot be joined / aligned with " + Describe(b) +
                   ". Only the same profile shape (Round, Rectangular or Oval) is supported; " +
                   "use a transition fitting between different shapes.";
        }

        /// <summary>
        /// Width / height directions of the duct cross-section. Horizontal-width convention for non-vertical ducts;
        /// for vertical ducts the connector X axis is used.
        /// </summary>
        public static void CrossSectionAxes(MEPCurve duct, XYZ u, out XYZ wDir, out XYZ hDir)
        {
            XYZ h = XYZ.BasisZ.CrossProduct(u);
            if (h.GetLength() < 1e-3)
            {
                h = XYZ.BasisX;
                foreach (Connector c in DuctUtil.GetConnectors(duct))
                {
                    if (!DuctUtil.IsPhysical(c)) continue;
                    try
                    {
                        XYZ bx = c.CoordinateSystem.BasisX;
                        XYZ proj = bx.Subtract(u.Multiply(bx.DotProduct(u)));
                        if (proj.GetLength() > 1e-3) { h = proj; break; }
                    }
                    catch { }
                }
                h = h.Subtract(u.Multiply(h.DotProduct(u)));
                if (h.GetLength() < 1e-6) h = u.CrossProduct(XYZ.BasisY);
            }
            wDir = h.Normalize();
            hDir = u.CrossProduct(wDir).Normalize();
        }

        /// <summary>Half extent of the cross-section along a direction n (perpendicular to the duct axis).</summary>
        public static double HalfExtent(DuctProfile p, XYZ wDir, XYZ hDir, XYZ n)
        {
            if (p == null || !p.IsKnown) return 0.01;
            if (p.Shape == DuctShape.Round) return Math.Max(p.Width / 2.0, 0.01);
            // Oval is approximated by its bounding rectangle (conservative for collision)
            double e = (p.Width / 2.0) * Math.Abs(wDir.DotProduct(n)) + (p.Height / 2.0) * Math.Abs(hDir.DotProduct(n));
            return Math.Max(e, 0.01);
        }
    }

    // ------------------------------------------------------------------------------
    //  Utilities: connectors, classification of elements
    // ------------------------------------------------------------------------------
    internal static class DuctUtil
    {
        public static bool IsRigidDuct(Element e)
        {
            Duct d = e as Duct;                      // FlexDuct is NOT a Duct
            if (d == null || e is FlexDuct) return false;
            return GetLine(d) != null;
        }

        public static bool IsFlexDuct(Element e) { return e is FlexDuct; }

        public static Line GetLine(MEPCurve p)
        {
            if (p == null) return null;
            LocationCurve lc = p.Location as LocationCurve;
            return lc != null ? lc.Curve as Line : null;
        }

        private static bool CatIs(Element e, BuiltInCategory bic)
        {
            return e != null && e.Category != null && e.Category.Id.Equals(new ElementId(bic));
        }

        public static bool IsDuctFitting(Element e) { return e is FamilyInstance && CatIs(e, BuiltInCategory.OST_DuctFitting); }
        public static bool IsDuctAccessory(Element e) { return e is FamilyInstance && CatIs(e, BuiltInCategory.OST_DuctAccessory); }
        public static bool IsAirTerminal(Element e) { return e is FamilyInstance && CatIs(e, BuiltInCategory.OST_DuctTerminal); }

        /// <summary>Elements that travel with the duct: duct fittings (elbows, tees, unions...) and air terminals.</summary>
        public static bool IsMover(Element e) { return IsDuctFitting(e) || IsAirTerminal(e); }

        /// <summary>Air terminals hosted on the duct (they move with it and are never boundaries).</summary>
        public static List<Element> HostedTerminals(Element duct)
        {
            var list = new List<Element>();
            if (duct == null) return list;
            try
            {
                foreach (ElementId id in duct.GetDependentElements(new ElementCategoryFilter(BuiltInCategory.OST_DuctTerminal)))
                {
                    FamilyInstance fi = duct.Document.GetElement(id) as FamilyInstance;
                    if (fi != null && fi.Host != null && fi.Host.Id.Equals(duct.Id)) list.Add(fi);
                }
            }
            catch { }
            return list;
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

        /// <summary>Only physical connectors support IsConnected / AllRefs (logical / reference connectors throw).</summary>
        public static bool IsPhysical(Connector c)
        {
            try
            {
                ConnectorType t = c.ConnectorType;
                return t == ConnectorType.End || t == ConnectorType.Curve || t == ConnectorType.Physical;
            }
            catch { return false; }
        }

        public static bool IsHvac(Connector c)
        {
            try { return c.Domain == Domain.DomainHvac; }
            catch { return false; }
        }

        /// <summary>Physical connector in the HVAC domain.</summary>
        public static bool IsUsable(Connector c) { return IsPhysical(c) && IsHvac(c); }

        public static List<Connector> GetRefs(Connector c, Element self)
        {
            var list = new List<Connector>();
            if (!IsUsable(c)) return list;
            bool connected;
            try { connected = c.IsConnected; } catch { return list; }
            if (!connected) return list;
            foreach (Connector other in c.AllRefs)
            {
                if (other.Owner == null) continue;
                if (other.Owner.Id.Equals(self.Id)) continue;
                if (other.Owner is MEPSystem) continue;
                if (!IsHvac(other)) continue;
                list.Add(other);
            }
            return list;
        }

        public static Connector FindConnector(Document doc, ElementId ownerId, int connId)
        {
            foreach (Connector c in GetConnectors(doc.GetElement(ownerId)))
                if (c.Id == connId) return c;
            return null;
        }

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

        public static XYZ CenterOf(Element e)
        {
            BoundingBoxXYZ bb = e.get_BoundingBox(null);
            if (bb != null) return bb.Min.Add(bb.Max).Multiply(0.5);
            LocationPoint lp = e.Location as LocationPoint;
            return lp != null ? lp.Point : XYZ.Zero;
        }

        public static string Describe(Document doc, ElementId id)
        {
            Element e = (id == null || id.Equals(ElementId.InvalidElementId)) ? null : doc.GetElement(id);
            if (e == null) return "—";
            string cat = e.Category != null ? e.Category.Name : "Element";
            return cat + " · " + e.Name + " [Id " + id + "]";
        }

        /// <summary>Halftone on/off for one element in a view, keeping its other overrides. Needs an open Transaction.</summary>
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
    }

    // ------------------------------------------------------------------------------
    //  Failure preprocessor
    // ------------------------------------------------------------------------------
    internal sealed class DuctRollbackPreprocessor : IFailuresPreprocessor
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
    //  Keyboard hook: Enter / Space confirm a PickObject / PickPoint by sending Esc
    // ------------------------------------------------------------------------------
    internal sealed class DuctKeyConfirmHook : IDisposable
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

        public DuctKeyConfirmHook()
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
    //  Selection filters
    // ------------------------------------------------------------------------------
    /// <summary>Accepts RIGID ducts only (flexible ducts are excluded).</summary>
    internal sealed class DuctSelectionFilter : ISelectionFilter
    {
        private readonly ElementId _exclude;
        public DuctSelectionFilter(ElementId exclude) { _exclude = exclude; }

        public bool AllowElement(Element e)
        {
            if (e.Id.Equals(_exclude)) return false;
            return DuctUtil.IsRigidDuct(e);
        }
        public bool AllowReference(Reference r, XYZ p) { return false; }
    }

    /// <summary>FamilyInstance with at least one physical HVAC connector (air terminal, accessory, equipment, fitting...).</summary>
    internal sealed class DuctFamilyFilter : ISelectionFilter
    {
        public bool AllowElement(Element e)
        {
            FamilyInstance fi = e as FamilyInstance;
            if (fi == null || fi.MEPModel == null) return false;
            foreach (Connector c in DuctUtil.GetConnectors(fi))
                if (DuctUtil.IsUsable(c)) return true;
            return false;
        }
        public bool AllowReference(Reference r, XYZ p) { return false; }
    }

    // ------------------------------------------------------------------------------
    //  DirectContext3D: ghost markers at each connector (origin cross + BasisZ arrow)
    // ------------------------------------------------------------------------------
    internal sealed class DuctMarkerServer : IDirectContext3DServer
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

        public DuctMarkerServer(List<XYZ> origins, List<XYZ> dirs)
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
            double baseL = minDist < double.MaxValue ? minDist * 0.4 : 120.0 * DuctCfg.MmToFt;
            baseL = Math.Max(30.0 * DuctCfg.MmToFt, Math.Min(180.0 * DuctCfg.MmToFt, baseL));

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
                double off = 20.0 * DuctCfg.MmToFt;
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
        public string GetName() { return "DuctAligner connector markers"; }
        public string GetVendorId() { return "DuctAligner"; }
        public string GetDescription() { return "Ghost markers for HVAC connectors."; }
        public string GetApplicationId() { return ""; }
        public string GetSourceId() { return ""; }
        public bool UsesHandles() { return false; }
    }

    // ------------------------------------------------------------------------------
    //  Picking: family + connector (shared by the 3 connector workflows and the palette)
    // ------------------------------------------------------------------------------
    internal static class DuctPick
    {
        public static List<Connector> ListConnectors(FamilyInstance fam)
        {
            return DuctUtil.GetConnectors(fam).Where(DuctUtil.IsUsable).OrderBy(c => c.Id).ToList();
        }

        public static bool PickFamilyAndConnector(UIDocument uidoc, Document doc, out FamilyInstance fam, out Connector conn)
        {
            fam = null;
            conn = null;
            try
            {
                Reference fr = uidoc.Selection.PickObject(ObjectType.Element, new DuctFamilyFilter(),
                    "Select the FAMILY (air terminal, accessory, equipment...) that has the reference HVAC connector");
                fam = doc.GetElement(fr.ElementId) as FamilyInstance;
                if (fam == null) return false;

                List<Connector> conns = ListConnectors(fam);
                if (conns.Count == 0)
                {
                    TaskDialog.Show(DuctCfg.Caption, "The family has no usable HVAC connectors.");
                    return false;
                }

                if (conns.Count == 1) conn = conns[0];
                else
                {
                    conn = ChooseConnector(uidoc, doc, fam, conns, ElementId.InvalidElementId);
                    if (conn == null) return false;
                }
                return true;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>
        /// DirectContext3D markers + PickPoint + hook. Highlighting and the temporary sketch plane live inside
        /// a TransactionGroup that is rolled back on exit.
        /// </summary>
        public static Connector ChooseConnector(UIDocument uidoc, Document doc, FamilyInstance fam,
            List<Connector> conns, ElementId referenceCurveId)
        {
            View view = doc.ActiveView;
            var server = new DuctMarkerServer(
                conns.Select(c => c.Origin).ToList(),
                conns.Select(c => c.CoordinateSystem.BasisZ).ToList());

            BoundingBoxXYZ bb = fam.get_BoundingBox(null);
            double planeZ = bb != null ? (bb.Min.Z + bb.Max.Z) / 2.0 : conns[0].Origin.Z;
            var planeOrigin = new XYZ(conns[0].Origin.X, conns[0].Origin.Y, planeZ);

            Connector chosen = null;
            DuctKeyConfirmHook hook = null;

            using (var tg = new TransactionGroup(doc, "Choose duct connector"))
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
                            var ductIds = new FilteredElementCollector(doc, view.Id)
                                .WhereElementIsNotElementType()
                                .WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory>
                                {
                                    BuiltInCategory.OST_DuctCurves,
                                    BuiltInCategory.OST_FlexDuctCurves
                                }))
                                .ToElementIds();
                            foreach (ElementId id in ductIds)
                            {
                                if (id.Equals(referenceCurveId)) continue;
                                view.SetElementOverrides(id, halfOgs);
                            }

                            if (!referenceCurveId.Equals(ElementId.InvalidElementId))
                            {
                                var refOgs = new OverrideGraphicSettings();
                                refOgs.SetProjectionLineColor(new Color(255, 140, 0));
                                view.SetElementOverrides(referenceCurveId, refOgs);
                            }

                            var ogs = new OverrideGraphicSettings();
                            ogs.SetProjectionLineColor(new Color(34, 184, 219));
                            ogs.SetSurfaceTransparency(40);
                            view.SetElementOverrides(fam.Id, ogs);
                        }
                        catch { }

                        t.Commit();
                    }

                    server.Register();
                    hook = new DuctKeyConfirmHook();
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
}
