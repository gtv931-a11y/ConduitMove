// =====================================================================================
//  AlignConnectorToPipe.cs
//
//  Author: Irving Cedeño
//
//  Inverse command to "Align To Connector": instead of moving the pipe/conduit toward
//  the connector, it MOVES THE FAMILY so the selected connector lands on the axis of
//  the reference pipe/conduit (which stays fixed).
//
//  Workflow:
//    1) Select the reference pipe/conduit.
//    2) Select the family (box, receptacle, equipment...).
//    3) If the family has 2 or more connectors: ghost arrows, click near one
//       (highlighted) and press Enter / Space to confirm.
//    4) The family moves in X, Y AND Z until the selected connector sits exactly on the
//       centre axis of the curve (horizontal curve: the family goes up/down as needed;
//       vertical curve: it moves in plan only, as before).
//       The move is a plain Revit move of the family: if the family cannot move freely
//       (it already has connections, it is pinned, etc.) Revit's own limitation applies
//       and Revit's message is shown. There are no extra checks or conflict windows.
//    5) If the connector diameter does not match the curve diameter, a palette
//       (RGB 59, 68, 83) appears with two options: change the curve or change the connector.
//
//  Reuses internal classes from aligntoconnector.cs (namespace AlignToConnector):
//  Cfg, KeyConfirmHook, ConnectorMarkerServer, ConnectorFamilyFilter, MepCurveSelectionFilter,
//  RollbackOnErrorPreprocessor, AlignEngine (GetConnectors) and AlinearConectorCommand.
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using AlignToConnector;

namespace ConnectorToPipe
{
    internal static class CtpCfg
    {
        public const string Caption = "Align Connector To Conduit/Pipe";

        /// <summary>Diameter difference (0.1 mm) below which sizes are considered equal.</summary>
        public const double DiameterTolFt = 0.1 / 304.8;
    }

    // ------------------------------------------------------------------------------
    //  Data
    // ------------------------------------------------------------------------------
    internal sealed class CtpSession
    {
        public Document Doc;
        public ElementId PipeId;        // reference pipe/conduit (fixed)
        public ElementId FamilyId;      // family to move
        public int ConnectorId;         // selected connector on the family
        public string Note;
    }

    // ------------------------------------------------------------------------------
    //  Engine
    // ------------------------------------------------------------------------------
    internal static class CtpEngine
    {
        // ------------------------- Utilities -------------------------

        private static Line GetLine(MEPCurve p)
        {
            LocationCurve lc = p.Location as LocationCurve;
            return lc != null ? lc.Curve as Line : null;
        }

        internal static Connector FindConnector(Document doc, ElementId ownerId, int connId)
        {
            foreach (Connector c in AlignEngine.GetConnectors(doc.GetElement(ownerId)))
                if (c.Id == connId) return c;
            return null;
        }

        // ------------------------- Main Flow -------------------------

        /// <summary>
        /// Moves the family so the connector lands on the curve axis.
        /// Returns null on success, or the message to show to the user (Revit's own message if Revit refused the move).
        /// </summary>
        public static string Run(CtpSession s)
        {
            s.Note = null;

            string err;
            XYZ d = ComputeDisplacement(s, out err);
            if (err != null) return err;

            if (d.GetLength() < Cfg.AlignTolFt)
            {
                s.Note = "The element was not moved: the connector is already on the curve axis " +
                         "(difference " + (d.GetLength() / Cfg.MmToFt).ToString("0.##") + " mm).";
                return null;
            }

            return MoveFamily(s, d);
        }

        /// <summary>Called when alignment finishes: checks diameters and shows pending note if no palette appeared.</summary>
        public static void Finish(CtpSession s)
        {
            bool shown = false;
            try { shown = ResolveDiameters(s); }
            catch (Exception ex)
            {
                TaskDialog.Show(CtpCfg.Caption, "Could not check diameters: " + ex.Message);
            }

            if (!shown && !string.IsNullOrEmpty(s.Note))
                TaskDialog.Show(CtpCfg.Caption, s.Note);
        }

        // ------------------------- Displacement -------------------------

        /// <summary>
        /// Full 3D displacement of the FAMILY so its connector lands on the centre axis of the curve:
        /// the shortest move from the connector to the (infinite) axis line.
        ///  - Horizontal curve: moves in plan AND in Z (up if the connector is below the axis, down if above).
        ///  - Vertical curve: the axis has no Z preference, so it moves in plan only (unchanged behaviour).
        /// </summary>
        private static XYZ ComputeDisplacement(CtpSession s, out string error)
        {
            error = null;
            Document doc = s.Doc;

            MEPCurve pipe = doc.GetElement(s.PipeId) as MEPCurve;
            Line axis = pipe != null ? GetLine(pipe) : null;
            if (axis == null)
            {
                error = "The reference pipe/conduit does not have a valid linear geometry.";
                return XYZ.Zero;
            }

            Connector conn = FindConnector(doc, s.FamilyId, s.ConnectorId);
            if (conn == null)
            {
                error = "The selected connector on the family could not be found.";
                return XYZ.Zero;
            }

            XYZ o = conn.Origin;
            XYZ p0 = axis.GetEndPoint(0);
            XYZ u = axis.Direction;
            XYZ onAxis = p0.Add(u.Multiply(o.Subtract(p0).DotProduct(u)));   // closest point on the infinite axis
            return onAxis.Subtract(o);
        }

        // ------------------------- Move (plain Revit move) -------------------------

        /// <summary>
        /// Plain Revit move of the family. Nothing is pre-checked: if Revit refuses (connected family,
        /// pinned element, etc.) the transaction is rolled back and Revit's own message is returned.
        /// </summary>
        private static string MoveFamily(CtpSession s, XYZ d)
        {
            Document doc = s.Doc;
            var pre = new RollbackOnErrorPreprocessor();

            using (var t = new Transaction(doc, "Align element to pipe/conduit"))
            {
                t.Start();
                try
                {
                    FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                    fho.SetFailuresPreprocessor(pre);
                    fho.SetClearAfterRollback(true);
                    t.SetFailureHandlingOptions(fho);

                    ElementTransformUtils.MoveElement(doc, s.FamilyId, d);

                    if (t.Commit() == TransactionStatus.Committed) return null;

                    return "Revit could not move the element." +
                           (string.IsNullOrEmpty(pre.ErrorText) ? "" : "\n\n" + pre.ErrorText);
                }
                catch (Exception ex)
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    return "Revit could not move the element.\n\n" + ex.Message;
                }
            }
        }

        // ------------------------- Diameters -------------------------

        private static double CurveDiameter(MEPCurve c)
        {
            BuiltInParameter bip = c is Conduit
                ? BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM
                : BuiltInParameter.RBS_PIPE_DIAMETER_PARAM;

            Parameter p = c.get_Parameter(bip);
            if (p != null && p.HasValue) return p.AsDouble();

            try
            {
                foreach (Connector cn in c.ConnectorManager.Connectors)
                    if (cn.Shape == ConnectorProfileType.Round) return cn.Radius * 2.0;
            }
            catch { }
            return 0;
        }

        /// <summary>Compares connector diameter with curve diameter. Returns true if the palette was shown.</summary>
        private static bool ResolveDiameters(CtpSession s)
        {
            Document doc = s.Doc;
            FamilyInstance fam = doc.GetElement(s.FamilyId) as FamilyInstance;
            MEPCurve pipe = doc.GetElement(s.PipeId) as MEPCurve;
            Connector conn = FindConnector(doc, s.FamilyId, s.ConnectorId);
            if (fam == null || pipe == null || conn == null) return false;

            double connDia;
            try
            {
                if (conn.Shape != ConnectorProfileType.Round) return false;   // round connectors only
                connDia = conn.Radius * 2.0;
            }
            catch { return false; }

            double tubeDia = CurveDiameter(pipe);
            if (tubeDia <= 0 || connDia <= 0) return false;
            if (Math.Abs(connDia - tubeDia) <= CtpCfg.DiameterTolFt) return false;

            var win = new DiameterChoiceWindow(pipe is Conduit, connDia, tubeDia);
            try { new WindowInteropHelper(win).Owner = Process.GetCurrentProcess().MainWindowHandle; }
            catch { }
            win.ShowDialog();

            string err = null;
            if (win.Choice == DiameterChoice.Curve)
                err = ApplyCurveDiameter(doc, pipe, connDia);
            else if (win.Choice == DiameterChoice.Connector)
                err = ApplyConnectorDiameter(doc, s.FamilyId, s.ConnectorId, tubeDia);

            if (err != null)
                TaskDialog.Show(CtpCfg.Caption, "Could not change diameter.\n\n" + err);
            return true;
        }

        private static string ApplyCurveDiameter(Document doc, MEPCurve c, double dia)
        {
            BuiltInParameter bip = c is Conduit
                ? BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM
                : BuiltInParameter.RBS_PIPE_DIAMETER_PARAM;

            Parameter p = c.get_Parameter(bip);
            if (p == null || p.IsReadOnly) return "The segment diameter parameter is read-only.";

            var pre = new RollbackOnErrorPreprocessor();
            using (var t = new Transaction(doc, "Match segment diameter to connector"))
            {
                t.Start();
                try
                {
                    FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                    fho.SetFailuresPreprocessor(pre);
                    fho.SetClearAfterRollback(true);
                    t.SetFailureHandlingOptions(fho);

                    if (!p.Set(dia))
                    {
                        t.RollBack();
                        return "Revit did not accept the new diameter.";
                    }

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

        private static string ApplyConnectorDiameter(Document doc, ElementId famId, int connId, double dia)
        {
            Connector conn = FindConnector(doc, famId, connId);
            if (conn == null) return "Connector not found.";

            var pre = new RollbackOnErrorPreprocessor();
            using (var t = new Transaction(doc, "Match connector diameter to pipe/conduit"))
            {
                t.Start();
                try
                {
                    FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                    fho.SetFailuresPreprocessor(pre);
                    fho.SetClearAfterRollback(true);
                    t.SetFailureHandlingOptions(fho);

                    conn.Radius = dia / 2.0;

                    if (t.Commit() != TransactionStatus.Committed)
                        return string.IsNullOrEmpty(pre.ErrorText) ? "Revit rejected the change." : pre.ErrorText;
                }
                catch (Exception ex)
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    return "This connector size cannot be changed from here (it is likely driven by a " +
                           "family parameter).\n" + ex.Message;
                }
            }

            Connector after = FindConnector(doc, famId, connId);
            if (after == null || Math.Abs(after.Radius * 2.0 - dia) > CtpCfg.DiameterTolFt)
                return "The connector size did not change (it is likely driven by a family parameter).";
            return null;
        }
    }

    // ------------------------------------------------------------------------------
    //  Diameter Palette: RGB(59, 68, 83), borderless, two options
    // ------------------------------------------------------------------------------
    internal enum DiameterChoice { Cancel, Curve, Connector }

    internal sealed class DiameterChoiceWindow : Window
    {
        public DiameterChoice Choice = DiameterChoice.Cancel;

        private const string ButtonStyleXaml =
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "      xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
            "  <Setter Property='Background' Value='Transparent'/>" +
            "  <Setter Property='BorderThickness' Value='0'/>" +
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
            "        </ControlTemplate.Triggers>" +
            "      </ControlTemplate>" +
            "    </Setter.Value>" +
            "  </Setter>" +
            "</Style>";

        private static string Mm(double feet)
        {
            return (feet * 304.8).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        public DiameterChoiceWindow(bool isConduit, double connDiaFt, double tubeDiaFt)
        {
            Title = "Mismatching Diameters";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowInTaskbar = false;
            Topmost = true;
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 68, 83));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            System.Windows.Style st =
                (System.Windows.Style)System.Windows.Markup.XamlReader.Parse(ButtonStyleXaml);
            Resources.Add(typeof(Button), st);

            string curveName = isConduit ? "conduit" : "pipe";
            string curveLabel = isConduit ? "Conduit" : "Pipe";

            var title = new TextBlock
            {
                Text = "Mismatching Diameters",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(140, 230, 170)),
                VerticalAlignment = VerticalAlignment.Center
            };

            var closeBtn = new Button
            {
                Width = 22,
                Height = 22,
                ToolTip = "Cancel",
                Content = new TextBlock
                {
                    Text = "×",
                    FontSize = 15,
                    Foreground = System.Windows.Media.Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            closeBtn.Click += (s, e) => { Choice = DiameterChoice.Cancel; Close(); };

            var headerRow = new System.Windows.Controls.DockPanel { LastChildFill = true };
            System.Windows.Controls.DockPanel.SetDock(closeBtn, System.Windows.Controls.Dock.Right);
            headerRow.Children.Add(closeBtn);
            headerRow.Children.Add(title);

            var header = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 56, 69)),
                Padding = new Thickness(8, 3, 4, 3),
                Child = headerRow
            };
            header.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };

            var info = new TextBlock
            {
                Text = "Connector Ø " + Mm(connDiaFt) + " mm   ·   " + curveLabel + " Ø " + Mm(tubeDiaFt) + " mm",
                FontSize = 12,
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(10, 8, 10, 4)
            };

            // Option 1: change pipe/conduit
            Button optCurve = MakeOption(
                "M 3,9 L 25,9 L 25,19 L 3,19 Z M 10,9 L 10,19",
                "Change " + curveName,
                "Ø " + Mm(tubeDiaFt) + " → " + Mm(connDiaFt) + " mm",
                DiameterChoice.Curve);

            // Option 2: change family connector
            Button optConn = MakeOption(
                "M 5,5 L 20,5 L 20,23 L 5,23 Z M 20,14 L 26,14",
                "Change connector",
                "Ø " + Mm(connDiaFt) + " → " + Mm(tubeDiaFt) + " mm",
                DiameterChoice.Connector);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 6, 8) };
            row.Children.Add(optCurve);
            row.Children.Add(optConn);

            var root = new StackPanel();
            root.Children.Add(header);
            root.Children.Add(info);
            root.Children.Add(row);

            Content = new Border
            {
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(90, 102, 122)),
                BorderThickness = new Thickness(1),
                Child = root
            };

            KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape) { Choice = DiameterChoice.Cancel; Close(); }
            };
        }

        private Button MakeOption(string iconPath, string title, string sub, DiameterChoice choice)
        {
            var icon = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(iconPath),
                Stroke = System.Windows.Media.Brushes.White,
                StrokeThickness = 2,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                Stretch = System.Windows.Media.Stretch.Uniform,
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.White
            });
            texts.Children.Add(new TextBlock
            {
                Text = sub,
                FontSize = 11,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(190, 200, 215))
            });

            var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 12, 0) };
            content.Children.Add(icon);
            content.Children.Add(texts);

            var b = new Button { Content = content, Width = 178, Height = 58 };
            b.Click += (s, e) => { Choice = choice; Close(); };
            return b;
        }
    }

    // ------------------------------------------------------------------------------
    //  Command
    // ------------------------------------------------------------------------------
    [Transaction(TransactionMode.Manual)]
    public class AlinearConectorATuberiaCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            MEPCurve pipe;
            FamilyInstance fam;
            Connector conn;

            try
            {
                // ---- 1) Reference pipe/conduit (does not move) ----
                Reference pr = uidoc.Selection.PickObject(ObjectType.Element,
                    new MepCurveSelectionFilter(ElementId.InvalidElementId),
                    "Select the REFERENCE pipe or conduit (the element will move toward it)");
                pipe = doc.GetElement(pr.ElementId) as MEPCurve;
                if (pipe == null) return Result.Cancelled;

                LocationCurve lc = pipe.Location as LocationCurve;
                if (lc == null || !(lc.Curve is Line))
                {
                    TaskDialog.Show(CtpCfg.Caption, "The selected segment is not a straight run.");
                    return Result.Cancelled;
                }

                // ---- 2) Family that will move ----
                Reference fr = uidoc.Selection.PickObject(ObjectType.Element, new ConnectorFamilyFilter(),
                    "Select the FAMILY to move toward the pipe/conduit");
                fam = doc.GetElement(fr.ElementId) as FamilyInstance;
                if (fam == null) return Result.Cancelled;

                // ---- 3) Connector ----
                List<Connector> conns = ListConnectors(fam, pipe);
                if (conns.Count == 0)
                {
                    TaskDialog.Show(CtpCfg.Caption, "The family has no usable connectors.");
                    return Result.Cancelled;
                }

                if (conns.Count == 1) conn = conns[0];   // single connector: automatic
                else
                {
                    conn = ChooseConnector(uidoc, doc, fam, conns, pipe.Id);
                    if (conn == null) return Result.Cancelled;
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            // ---- 4) Move the family (plain Revit move; Revit's own message if it refuses) ----
            var session = new CtpSession
            {
                Doc = doc,
                PipeId = pipe.Id,
                FamilyId = fam.Id,
                ConnectorId = conn.Id
            };

            string error;
            try { error = CtpEngine.Run(session); }
            catch (Exception ex) { error = ex.Message; }

            if (error != null)
            {
                TaskDialog.Show(CtpCfg.Caption, error);
                return Result.Cancelled;
            }

            CtpEngine.Finish(session);

            // Alignment finished (after the diameter check): open the floating palette pinned to this family,
            // connector and conduit, so Enter / Space stretches that same conduit to the connector.
            try
            {
                AlignElementsSuite.ConnectorPalette.ShowFor(uidoc, fam.Id, conn.Id, pipe.Id);
            }
            catch { }

            return Result.Succeeded;
        }


        // ------------------------- Family connectors -------------------------

        /// <summary>Usable connectors; if any match the curve domain (piping / conduit), returns only those.</summary>
        private static List<Connector> ListConnectors(FamilyInstance fam, MEPCurve pipe)
        {
            List<Connector> all = AlignEngine.GetConnectors(fam)
                .Where(AlinearConectorCommand.IsUsableDomain).ToList();

            Domain want = pipe is Pipe ? Domain.DomainPiping : Domain.DomainCableTrayConduit;
            List<Connector> match = all.Where(k => k.Domain == want).ToList();
            return match.Count > 0 ? match : all;
        }

        // ------------------------- Choose connector (2 or more) -------------------------

        /// <summary>
        /// DirectContext3D ghost arrows + PickPoint + KeyConfirmHook (Enter / Space confirm).
        /// Highlighting and temporary sketch plane live in a TransactionGroup rolled back on exit.
        /// </summary>
        private static Connector ChooseConnector(UIDocument uidoc, Document doc, FamilyInstance fam,
            List<Connector> conns, ElementId pipeId)
        {
            View view = doc.ActiveView;
            var server = new ConnectorMarkerServer(
                conns.Select(k => k.Origin).ToList(),
                conns.Select(k => k.CoordinateSystem.BasisZ).ToList());

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
                            // Temporary plane so PickPoint works in 3D views
                            SketchPlane sp = SketchPlane.Create(doc,
                                Plane.CreateByNormalAndOrigin(XYZ.BasisZ, planeOrigin));
                            view.SketchPlane = sp;
                        }
                        catch { }

                        try
                        {
                            // Pipes/conduits halftone; reference curve orange; family cyan
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
                            {
                                if (id.Equals(pipeId)) continue;
                                view.SetElementOverrides(id, halfOgs);
                            }

                            var pipeOgs = new OverrideGraphicSettings();
                            pipeOgs.SetProjectionLineColor(new Color(255, 140, 0));
                            view.SetElementOverrides(pipeId, pipeOgs);

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
                            // Enter/Space (hook injects Esc to exit PickPoint) => confirms
                            if (hook.Confirmed && server.Selected >= 0) chosen = conns[server.Selected];
                            break;   // Real Esc => chosen remains null (cancels)
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

        /// <summary>Connector closest to the view RAY passing through the click (avoids parallax in 3D views).</summary>
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