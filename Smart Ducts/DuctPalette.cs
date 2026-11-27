// =====================================================================================
//  DuctPalette.cs   (namespace DuctAlignSuite)
//
//  Floating, non-modal palette of the DUCT module. Same behavior as AlignElementsSuite.ConnectorPalette
//  but with its OWN static state, its OWN green marker server and its OWN window, so it never interferes
//  with the pipe / conduit palette (both can coexist).
//
//  Buttons: Align Duct To Connector | Stretch Duct to Connector | Align Connector To Duct |
//           Change Connector | Close.
//  Icons: DuctAlignToConnector.png, DuctStretchToConnector.png, DuctConnectorToDuct.png,
//         DuctChangeConnector.png (each falls back to the existing icon files when missing).
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;
using Autodesk.Revit.UI;
using DuctAligner;

namespace DuctAlignSuite
{
    internal enum DuctPaletteAction
    {
        AlignToConnector,
        StretchToConnector,
        ConnectorToDuct,
        ChangeConnector
    }

    internal static class DuctPalette
    {
        internal static Document ActiveDoc;
        internal static ElementId ActiveFamilyId = ElementId.InvalidElementId;
        internal static int ActiveConnectorId = -1;
        internal static ElementId ActiveCurveId = ElementId.InvalidElementId;

        private static DuctPaletteWindow _window;
        private static ExternalEvent _extEvent;
        private static DuctPaletteHandler _handler;
        private static DuctPinnedMarkerServer _markerServer;

        public static void ShowFor(UIDocument uidoc, ElementId famId, int connId, ElementId curveId = null)
        {
            if (uidoc == null || uidoc.Document == null || famId == null || connId < 0) return;

            bool sameConnector = ActiveDoc != null && ActiveDoc.Equals(uidoc.Document)
                                 && ActiveFamilyId.Equals(famId) && ActiveConnectorId == connId;

            ActiveDoc = uidoc.Document;
            ActiveFamilyId = famId;
            ActiveConnectorId = connId;
            if (curveId != null) ActiveCurveId = curveId;
            else if (!sameConnector) ActiveCurveId = ElementId.InvalidElementId;

            FamilyInstance fam = ActiveDoc.GetElement(famId) as FamilyInstance;
            Connector conn = FindConnector(fam, connId);
            if (fam == null || conn == null) return;

            UpdateMarker(uidoc, conn);

            if (_handler == null)
            {
                _handler = new DuctPaletteHandler();
                _extEvent = ExternalEvent.Create(_handler);
            }

            if (_window == null)
            {
                _window = new DuctPaletteWindow();
                _window.RequestAction = act =>
                {
                    if (_handler == null || _extEvent == null) return;
                    _handler.Action = act;
                    _window.SetBusy(true);
                    _extEvent.Raise();
                };
                _window.Closed += (s, e) =>
                {
                    ClearMarker(uidoc);
                    _window = null;
                };
                try { new WindowInteropHelper(_window).Owner = Process.GetCurrentProcess().MainWindowHandle; }
                catch { }
                _window.Show();
            }

            string famLabel = fam.Symbol != null && fam.Symbol.Family != null
                ? fam.Symbol.Family.Name
                : fam.Name;
            _window.SetConnectorInfo(famLabel, connId);
            _window.FocusPalette();
        }

        internal static Connector FindConnector(FamilyInstance fi, int connId)
        {
            if (fi == null || fi.MEPModel == null || fi.MEPModel.ConnectorManager == null) return null;
            foreach (Connector c in fi.MEPModel.ConnectorManager.Connectors)
                if (c.Id == connId) return c;
            return null;
        }

        internal static void UpdateMarker(UIDocument uidoc, Connector conn)
        {
            ClearMarker(null);
            if (conn == null) return;
            _markerServer = new DuctPinnedMarkerServer(conn.Origin, conn.CoordinateSystem.BasisZ);
            _markerServer.Register();
            try { if (uidoc != null) uidoc.RefreshActiveView(); } catch { }
        }

        internal static void ClearMarker(UIDocument uidoc)
        {
            if (_markerServer != null)
            {
                _markerServer.Unregister();
                _markerServer = null;
                try { if (uidoc != null) uidoc.RefreshActiveView(); } catch { }
            }
        }

        internal static void RefreshInfo(UIDocument uidoc)
        {
            if (_window == null || ActiveDoc == null) return;
            FamilyInstance fam = ActiveDoc.GetElement(ActiveFamilyId) as FamilyInstance;
            Connector conn = FindConnector(fam, ActiveConnectorId);
            if (fam == null || conn == null)
            {
                _window.Close();
                return;
            }
            UpdateMarker(uidoc, conn);
            string famLabel = fam.Symbol != null && fam.Symbol.Family != null
                ? fam.Symbol.Family.Name
                : fam.Name;
            _window.SetConnectorInfo(famLabel, conn.Id);
        }

        internal static DuctPaletteWindow Window { get { return _window; } }
    }

    internal sealed class DuctPaletteHandler : IExternalEventHandler
    {
        public DuctPaletteAction Action;

        public void Execute(UIApplication app)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            DuctPaletteWindow win = DuctPalette.Window;

            try
            {
                if (uidoc == null || uidoc.Document == null) return;
                Document doc = uidoc.Document;

                if (!doc.Equals(DuctPalette.ActiveDoc))
                {
                    TaskDialog.Show(DuctCfg.Caption, "The active document changed. Select a connector in this project.");
                    if (win != null) win.Close();
                    return;
                }

                if (Action == DuctPaletteAction.ChangeConnector)
                {
                    FamilyInstance newFam;
                    Connector newConn;
                    if (DuctPick.PickFamilyAndConnector(uidoc, doc, out newFam, out newConn))
                        DuctPalette.ShowFor(uidoc, newFam.Id, newConn.Id);
                    return;
                }

                FamilyInstance fam = doc.GetElement(DuctPalette.ActiveFamilyId) as FamilyInstance;
                Connector conn = DuctPalette.FindConnector(fam, DuctPalette.ActiveConnectorId);
                if (fam == null || conn == null)
                {
                    TaskDialog.Show(DuctCfg.Caption, "The pinned family or connector no longer exists in the model.");
                    if (win != null) win.Close();
                    return;
                }

                string msg = null;
                switch (Action)
                {
                    case DuctPaletteAction.AlignToConnector:
                        DuctAlignToConnectorCommand.ExecuteWithConnector(uidoc, fam, conn, ref msg);
                        break;
                    case DuctPaletteAction.StretchToConnector:
                        DuctStretchToConnectorCommand.ExecuteWithConnector(uidoc, fam, conn, ref msg);
                        break;
                    case DuctPaletteAction.ConnectorToDuct:
                        DuctConnectorToDuctCommand.ExecuteWithConnector(uidoc, fam, conn, ref msg);
                        break;
                }

                DuctPalette.RefreshInfo(uidoc);
            }
            catch (Exception ex)
            {
                TaskDialog.Show(DuctCfg.Caption, "Error executing command: " + ex.Message);
            }
            finally
            {
                if (DuctPalette.Window != null)
                {
                    DuctPalette.Window.SetBusy(false);
                    DuctPalette.Window.FocusPalette();
                }
            }
        }

        public string GetName() { return "DuctAlignSuite.PaletteHandler"; }
    }

    // ------------------------------------------------------------------------------
    //  Window: single-row icon bar, RGB(59, 68, 83)
    // ------------------------------------------------------------------------------
    internal sealed class DuctPaletteWindow : Window
    {
        private static readonly System.Windows.Media.Color BgColor =
            System.Windows.Media.Color.FromRgb(59, 68, 83);

        private readonly TextBlock _info = new TextBlock();
        private readonly Button _btnAlign;
        private readonly Button _btnStretch;
        private readonly Button _btnConnToDuct;
        private readonly Button _btnChange;
        private readonly Button _btnClose;

        public Action<DuctPaletteAction> RequestAction;

        private const string ButtonStyleXaml =
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "      xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
            "  <Setter Property='Background' Value='Transparent'/>" +
            "  <Setter Property='BorderThickness' Value='0'/>" +
            "  <Setter Property='Width' Value='40'/>" +
            "  <Setter Property='Height' Value='40'/>" +
            "  <Setter Property='Margin' Value='2'/>" +
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

        public DuctPaletteWindow()
        {
            Title = "Active Duct Connector";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowInTaskbar = false;
            Focusable = true;
            Background = new SolidColorBrush(BgColor);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 40;
            Top = 220;   // below the pipe / conduit palette so both can be open

            System.Windows.Style btnStyle =
                (System.Windows.Style)System.Windows.Markup.XamlReader.Parse(ButtonStyleXaml);
            Resources.Add(typeof(Button), btnStyle);

            string dir = "";
            try
            {
                dir = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
            }
            catch { }

            _btnAlign = MakeButton(PngIcon(dir, "DuctAlignToConnector.png", "AlignToConnector.png"),
                "Align Duct To Connector", DuctPaletteAction.AlignToConnector);
            _btnStretch = MakeButton(PngIcon(dir, "DuctStretchToConnector.png", "StretchToConnector.png"),
                "Stretch Duct to Connector", DuctPaletteAction.StretchToConnector);
            _btnConnToDuct = MakeButton(PngIcon(dir, "DuctConnectorToDuct.png", "AlignConnetorToConduit.png"),
                "Align Connector Element to Duct", DuctPaletteAction.ConnectorToDuct);
            _btnChange = MakeButton(PngIcon(dir, "DuctChangeConnector.png", "change_connectors.png"),
                "Change Connector", DuctPaletteAction.ChangeConnector);
            _btnClose = new Button { ToolTip = "Close", Content = VectorIcon("M 6,6 L 24,24 M 24,6 L 6,24") };
            _btnClose.Click += (s, e) => Close();

            var header = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 56, 69)),
                Padding = new Thickness(8, 4, 8, 4)
            };
            _info.FontSize = 12;
            _info.FontWeight = FontWeights.SemiBold;
            _info.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(140, 230, 170));
            _info.TextTrimming = TextTrimming.CharacterEllipsis;
            header.Child = _info;
            header.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
            row.Children.Add(_btnAlign);
            row.Children.Add(_btnStretch);
            row.Children.Add(_btnConnToDuct);
            row.Children.Add(_btnChange);
            row.Children.Add(_btnClose);

            var root = new StackPanel();
            root.Children.Add(header);
            root.Children.Add(row);

            Content = new Border
            {
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(90, 102, 122)),
                BorderThickness = new Thickness(1),
                Child = root
            };

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Return || e.Key == Key.Space)
                {
                    e.Handled = true;
                    Close();
                }
            };
        }

        private Button MakeButton(UIElement icon, string tip, DuctPaletteAction act)
        {
            var b = new Button { Content = icon, ToolTip = tip };
            b.Click += (s, e) => Fire(act);
            return b;
        }

        private static UIElement PngIcon(string dir, string file, string fallback)
        {
            foreach (string name in new[] { file, fallback })
            {
                try
                {
                    string p = System.IO.Path.Combine(dir, name);
                    if (!System.IO.File.Exists(p)) continue;
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(p, UriKind.Absolute);
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    return new System.Windows.Controls.Image { Source = bmp, Width = 28, Height = 28 };
                }
                catch { }
            }
            return new TextBlock { Text = "?", Foreground = Brushes.White, FontSize = 16 };
        }

        private static UIElement VectorIcon(string pathData)
        {
            return new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(pathData),
                Stroke = Brushes.White,
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Stretch = Stretch.Uniform,
                Width = 20,
                Height = 20
            };
        }

        public void SetConnectorInfo(string familyName, int connId)
        {
            _info.Text = "DUCT · " + familyName + " · Connector #" + connId;
        }

        public void FocusPalette()
        {
            try
            {
                Activate();
                Focus();
            }
            catch { }
        }

        public void SetBusy(bool busy)
        {
            _btnAlign.IsEnabled = !busy;
            _btnStretch.IsEnabled = !busy;
            _btnConnToDuct.IsEnabled = !busy;
            _btnChange.IsEnabled = !busy;
            _btnClose.IsEnabled = !busy;
        }

        private void Fire(DuctPaletteAction act)
        {
            if (RequestAction != null) RequestAction(act);
        }
    }

    // ------------------------------------------------------------------------------
    //  Green marker on the pinned connector while the duct palette is open
    // ------------------------------------------------------------------------------
    internal sealed class DuctPinnedMarkerServer : IDirectContext3DServer
    {
        private readonly Guid _id = Guid.NewGuid();
        private readonly XYZ _origin;
        private readonly XYZ _dir;
        private MultiServerService _service;
        private VertexBuffer _vb;
        private IndexBuffer _ib;
        private VertexFormat _vf;
        private EffectInstance _ei;
        private int _lineCount;
        private bool _built;

        public DuctPinnedMarkerServer(XYZ origin, XYZ dir)
        {
            _origin = origin;
            _dir = dir.Normalize();
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
            var col = new ColorWithTransparency(0, 210, 90, 0);
            Action<XYZ, XYZ> add = (a, b) => segs.Add(new[] { a, b });

            double L = 140.0 / 304.8;
            double r = 0.35 * L;
            add(_origin.Subtract(XYZ.BasisX.Multiply(r)), _origin.Add(XYZ.BasisX.Multiply(r)));
            add(_origin.Subtract(XYZ.BasisY.Multiply(r)), _origin.Add(XYZ.BasisY.Multiply(r)));
            add(_origin.Subtract(XYZ.BasisZ.Multiply(r)), _origin.Add(XYZ.BasisZ.Multiply(r)));

            XYZ u1 = _dir.CrossProduct(XYZ.BasisZ);
            if (u1.GetLength() < 1e-3) u1 = _dir.CrossProduct(XYZ.BasisX);
            u1 = u1.Normalize();
            XYZ u2 = _dir.CrossProduct(u1);

            XYZ tip = _origin.Add(_dir.Multiply(L));
            double off = 15.0 / 304.8;
            for (int k = -1; k <= 1; k++)
            {
                XYZ sh = u1.Multiply(k * off);
                add(_origin.Add(sh), tip.Add(sh));
            }
            XYZ back = tip.Subtract(_dir.Multiply(0.25 * L));
            double w = 0.12 * L;
            add(tip, back.Add(u1.Multiply(w)));
            add(tip, back.Subtract(u1.Multiply(w)));
            add(tip, back.Add(u2.Multiply(w)));
            add(tip, back.Subtract(u2.Multiply(w)));

            _lineCount = segs.Count;
            int vCount = _lineCount * 2;
            int vSize = vCount * VertexPositionColored.GetSizeInFloats();
            _vf = new VertexFormat(VertexFormatBits.PositionColored);
            _vb = new VertexBuffer(vSize);
            _vb.Map(vSize);
            VertexStreamPositionColored vs = _vb.GetVertexStreamPositionColored();
            for (int i = 0; i < _lineCount; i++)
            {
                vs.AddVertex(new VertexPositionColored(segs[i][0], col));
                vs.AddVertex(new VertexPositionColored(segs[i][1], col));
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
            double pad = 2.0;
            return new Outline(
                new XYZ(_origin.X - pad, _origin.Y - pad, _origin.Z - pad),
                new XYZ(_origin.X + pad, _origin.Y + pad, _origin.Z + pad));
        }

        public Guid GetServerId() { return _id; }
        public ExternalServiceId GetServiceId() { return ExternalServices.BuiltInExternalServices.DirectContext3DService; }
        public string GetName() { return "Duct pinned connector marker"; }
        public string GetVendorId() { return "DuctAligner"; }
        public string GetDescription() { return "Green marker for the HVAC connector pinned in the duct palette."; }
        public string GetApplicationId() { return ""; }
        public string GetSourceId() { return ""; }
        public bool UsesHandles() { return false; }
    }
}
