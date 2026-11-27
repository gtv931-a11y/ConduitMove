// =====================================================================================
//  DuctStretchAndConnectorToDuct.cs   (namespace DuctAligner)
//
//    (c) DuctStretchToConnectorCommand : stretches the nearest end of ONE rigid duct to the chosen HVAC
//        connector (projected on the duct axis so it never skews; connected automatically when coaxial
//        and the profile SHAPE is compatible).               [stretchconnectors pattern]
//
//    (d) DuctConnectorToDuctCommand    : MOVES THE FAMILY so the chosen connector lands on the axis of a
//        reference rigid duct (the duct stays fixed). If sizes differ (same shape) a palette offers to
//        change the duct size or the connector size. Different shapes are blocked.
//                                                              [AlignConnectorToPipe pattern]
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
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace DuctAligner
{
    // ==============================================================================
    //  (c) Stretch duct to connector
    // ==============================================================================
    [Transaction(TransactionMode.Manual)]
    public class DuctStretchToConnectorCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            FamilyInstance fam;
            Connector conn;
            if (!DuctPick.PickFamilyAndConnector(uidoc, doc, out fam, out conn))
                return Result.Cancelled;

            return ExecuteWithConnector(uidoc, fam, conn, ref message);
        }

        /// <summary>Also used by the floating palette.</summary>
        internal static bool PickFamilyAndConnector(UIDocument uidoc, Document doc, out FamilyInstance fam, out Connector conn)
        {
            return DuctPick.PickFamilyAndConnector(uidoc, doc, out fam, out conn);
        }

        internal static Result ExecuteWithConnector(UIDocument uidoc, FamilyInstance fam, Connector conn, ref string message)
        {
            Document doc = uidoc.Document;
            MEPCurve curve = null;
            try
            {
                Reference tr = uidoc.Selection.PickObject(ObjectType.Element,
                    new DuctSelectionFilter(fam.Id),
                    "Select the rigid duct to stretch to the connector");
                curve = doc.GetElement(tr.ElementId) as MEPCurve;
                if (curve == null || !DuctUtil.IsRigidDuct(curve)) return Result.Cancelled;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            string error;
            if (!StretchToConnector(doc, curve, conn, out error))
            {
                if (!string.IsNullOrEmpty(error))
                    TaskDialog.Show(DuctCfg.Caption, error);
                return Result.Cancelled;
            }

            try { DuctAlignSuite.DuctPalette.ShowFor(uidoc, fam.Id, conn.Id, curve.Id); }
            catch { }

            return Result.Succeeded;
        }

        private static bool StretchToConnector(Document doc, MEPCurve curve, Connector targetConn, out string error)
        {
            error = null;

            // ----- Profile SHAPE check (sizes may differ) -----
            DuctProfile dp = DuctProfiles.FromDuct(curve);
            DuctProfile cp = DuctProfiles.FromConnector(targetConn);
            if (!DuctProfiles.ShapeCompatible(dp, cp))
            {
                error = DuctProfiles.MismatchMessage(dp, cp);
                return false;
            }

            LocationCurve lc = curve.Location as LocationCurve;
            Line line = lc != null ? lc.Curve as Line : null;
            if (line == null)
            {
                error = "The selected duct does not have valid linear geometry.";
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
                error = "The current length of the duct is too small.";
                return false;
            }
            dir = dir.Divide(curLen);

            double newLen = targetPos.Subtract(pFar).DotProduct(dir);
            if (newLen < DuctCfg.MinCurveLengthMm * DuctCfg.MmToFt)
            {
                error = "The connector is behind the opposite end; the duct would become inverted or zero length.";
                return false;
            }

            XYZ pNew = pFar.Add(dir.Multiply(newLen));
            if (pNew.DistanceTo(pNear) < 0.05 * DuctCfg.MmToFt)
                return true;

            Connector nearConn = FindConnectorAt(curve, pNear);
            if (nearConn != null && DuctUtil.IsPhysical(nearConn) && nearConn.IsConnected)
            {
                error = "The nearest end of the duct is already connected to another element.";
                return false;
            }

            var pre = new DuctRollbackPreprocessor();
            using (var t = new Transaction(doc, "Stretch duct to connector"))
            {
                t.Start();
                try
                {
                    FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                    fho.SetFailuresPreprocessor(pre);
                    fho.SetClearAfterRollback(true);
                    t.SetFailureHandlingOptions(fho);

                    Line newLine = nearIdx == 0 ? Line.CreateBound(pNew, pFar) : Line.CreateBound(pFar, pNew);
                    lc.Curve = newLine;
                    doc.Regenerate();

                    // Auto-connect only if coaxial, same domain and compatible profile SHAPE
                    if (DuctUtil.IsUsable(targetConn) && !targetConn.IsConnected &&
                        pNew.DistanceTo(targetPos) <= DuctCfg.ConnectTolMm * DuctCfg.MmToFt)
                    {
                        Connector updatedNear = FindConnectorAt(curve, pNew);
                        if (updatedNear != null && DuctUtil.IsUsable(updatedNear) && !updatedNear.IsConnected &&
                            DuctProfiles.Compatible(updatedNear, targetConn))
                        {
                            try { updatedNear.ConnectTo(targetConn); }
                            catch { }
                        }
                    }

                    if (t.Commit() == TransactionStatus.Committed) return true;

                    error = "Revit rejected the stretch" +
                            (string.IsNullOrEmpty(pre.ErrorText) ? "." : ": " + pre.ErrorText);
                    return false;
                }
                catch (Exception ex)
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    error = "Could not stretch the duct: " + ex.Message;
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
    }

    // ==============================================================================
    //  (d) Align connector (family) to duct axis
    // ==============================================================================
    internal sealed class DuctCtdSession
    {
        public Document Doc;
        public ElementId DuctId;        // reference duct (fixed)
        public ElementId FamilyId;      // family to move
        public int ConnectorId;
        public string Note;
    }

    internal enum DuctSizeChoice { Cancel, Duct, Connector }

    internal static class DuctCtdEngine
    {
        public static string Run(DuctCtdSession s)
        {
            s.Note = null;

            string err;
            XYZ d = ComputeDisplacement(s, out err);
            if (err != null) return err;

            if (d.GetLength() < DuctCfg.AlignTolFt)
            {
                s.Note = "The element was not moved: the connector is already on the duct axis " +
                         "(difference " + (d.GetLength() / DuctCfg.MmToFt).ToString("0.##") + " mm).";
                return null;
            }

            return MoveFamily(s, d);
        }

        public static void Finish(DuctCtdSession s)
        {
            bool shown = false;
            try { shown = ResolveSizes(s); }
            catch (Exception ex)
            {
                TaskDialog.Show(DuctCfg.Caption, "Could not check sizes: " + ex.Message);
            }

            if (!shown && !string.IsNullOrEmpty(s.Note))
                TaskDialog.Show(DuctCfg.Caption, s.Note);
        }

        /// <summary>Profile SHAPE gate, executed BEFORE moving anything. Returns an error text or null.</summary>
        public static string CheckShape(Document doc, ElementId ductId, ElementId famId, int connId)
        {
            MEPCurve duct = doc.GetElement(ductId) as MEPCurve;
            Connector conn = DuctUtil.FindConnector(doc, famId, connId);
            if (duct == null || conn == null) return "The duct or the connector could not be found.";

            DuctProfile dp = DuctProfiles.FromDuct(duct);
            DuctProfile cp = DuctProfiles.FromConnector(conn);
            if (!DuctProfiles.ShapeCompatible(dp, cp))
                return DuctProfiles.MismatchMessage(cp, dp);
            return null;
        }

        /// <summary>Shortest move from the connector to the (infinite) duct axis; horizontal ducts also move in Z.</summary>
        private static XYZ ComputeDisplacement(DuctCtdSession s, out string error)
        {
            error = null;
            Document doc = s.Doc;

            MEPCurve duct = doc.GetElement(s.DuctId) as MEPCurve;
            Line axis = DuctUtil.GetLine(duct);
            if (axis == null)
            {
                error = "The reference duct does not have a valid linear geometry.";
                return XYZ.Zero;
            }

            Connector conn = DuctUtil.FindConnector(doc, s.FamilyId, s.ConnectorId);
            if (conn == null)
            {
                error = "The selected connector on the family could not be found.";
                return XYZ.Zero;
            }

            XYZ o = conn.Origin;
            XYZ p0 = axis.GetEndPoint(0);
            XYZ u = axis.Direction;
            XYZ onAxis = p0.Add(u.Multiply(o.Subtract(p0).DotProduct(u)));
            return onAxis.Subtract(o);
        }

        private static string MoveFamily(DuctCtdSession s, XYZ d)
        {
            Document doc = s.Doc;
            var pre = new DuctRollbackPreprocessor();

            using (var t = new Transaction(doc, "Align element to duct"))
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

        // ------------------------- Sizes (same shape, different dimensions) -------------------------

        private static bool ResolveSizes(DuctCtdSession s)
        {
            Document doc = s.Doc;
            MEPCurve duct = doc.GetElement(s.DuctId) as MEPCurve;
            Connector conn = DuctUtil.FindConnector(doc, s.FamilyId, s.ConnectorId);
            if (duct == null || conn == null) return false;

            DuctProfile dp = DuctProfiles.FromDuct(duct);
            DuctProfile cp = DuctProfiles.FromConnector(conn);
            if (!DuctProfiles.ShapeCompatible(dp, cp)) return false;   // already blocked before moving
            if (DuctProfiles.SameSize(dp, cp)) return false;

            var win = new DuctSizeChoiceWindow(dp, cp);
            try { new WindowInteropHelper(win).Owner = Process.GetCurrentProcess().MainWindowHandle; }
            catch { }
            win.ShowDialog();

            string err = null;
            if (win.Choice == DuctSizeChoice.Duct)
                err = ApplyDuctSize(doc, duct, cp);
            else if (win.Choice == DuctSizeChoice.Connector)
                err = ApplyConnectorSize(doc, s.FamilyId, s.ConnectorId, dp);

            if (err != null)
                TaskDialog.Show(DuctCfg.Caption, "Could not change size.\n\n" + err);
            return true;
        }

        private static string ApplyDuctSize(Document doc, MEPCurve duct, DuctProfile target)
        {
            var pre = new DuctRollbackPreprocessor();
            using (var t = new Transaction(doc, "Match duct size to connector"))
            {
                t.Start();
                try
                {
                    FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                    fho.SetFailuresPreprocessor(pre);
                    fho.SetClearAfterRollback(true);
                    t.SetFailureHandlingOptions(fho);

                    bool ok;
                    if (target.Shape == DuctShape.Round)
                    {
                        Parameter p = duct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                        ok = p != null && !p.IsReadOnly && p.Set(target.Width);
                    }
                    else
                    {
                        Parameter w = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                        Parameter h = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                        ok = w != null && h != null && !w.IsReadOnly && !h.IsReadOnly &&
                             w.Set(target.Width) && h.Set(target.Height);
                    }

                    if (!ok)
                    {
                        t.RollBack();
                        return "The duct size parameters are read-only or Revit did not accept the new size.";
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

        private static string ApplyConnectorSize(Document doc, ElementId famId, int connId, DuctProfile target)
        {
            Connector conn = DuctUtil.FindConnector(doc, famId, connId);
            if (conn == null) return "Connector not found.";

            var pre = new DuctRollbackPreprocessor();
            using (var t = new Transaction(doc, "Match connector size to duct"))
            {
                t.Start();
                try
                {
                    FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                    fho.SetFailuresPreprocessor(pre);
                    fho.SetClearAfterRollback(true);
                    t.SetFailureHandlingOptions(fho);

                    if (target.Shape == DuctShape.Round)
                    {
                        conn.Radius = target.Width / 2.0;
                    }
                    else
                    {
                        conn.Width = target.Width;
                        conn.Height = target.Height;
                    }

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

            Connector after = DuctUtil.FindConnector(doc, famId, connId);
            if (after == null || !DuctProfiles.SameSize(DuctProfiles.FromConnector(after), target))
                return "The connector size did not change (it is likely driven by a family parameter).";
            return null;
        }
    }

    // ------------------------------------------------------------------------------
    //  Size palette: RGB(59, 68, 83), two options
    // ------------------------------------------------------------------------------
    internal sealed class DuctSizeChoiceWindow : Window
    {
        public DuctSizeChoice Choice = DuctSizeChoice.Cancel;

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

        private static string Size(DuctProfile p)
        {
            string a = (p.Width * 304.8).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            if (p.Shape == DuctShape.Round) return "Ø " + a;
            string b = (p.Height * 304.8).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            return a + " × " + b;
        }

        public DuctSizeChoiceWindow(DuctProfile duct, DuctProfile conn)
        {
            Title = "Mismatching Sizes";
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

            var title = new TextBlock
            {
                Text = "Mismatching Sizes (" + DuctProfiles.ShapeName(duct.Shape) + ")",
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
            closeBtn.Click += (s, e) => { Choice = DuctSizeChoice.Cancel; Close(); };

            var headerRow = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(closeBtn, Dock.Right);
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
                Text = "Connector " + Size(conn) + " mm   ·   Duct " + Size(duct) + " mm",
                FontSize = 12,
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(10, 8, 10, 4)
            };

            Button optDuct = MakeOption(
                "M 3,9 L 25,9 L 25,19 L 3,19 Z M 10,9 L 10,19",
                "Change duct",
                Size(duct) + " → " + Size(conn) + " mm",
                DuctSizeChoice.Duct);

            Button optConn = MakeOption(
                "M 5,5 L 20,5 L 20,23 L 5,23 Z M 20,14 L 26,14",
                "Change connector",
                Size(conn) + " → " + Size(duct) + " mm",
                DuctSizeChoice.Connector);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 6, 8) };
            row.Children.Add(optDuct);
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
                if (e.Key == System.Windows.Input.Key.Escape) { Choice = DuctSizeChoice.Cancel; Close(); }
            };
        }

        private Button MakeOption(string iconPath, string title, string sub, DuctSizeChoice choice)
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

            var b = new Button { Content = content, Width = 200, Height = 58 };
            b.Click += (s, e) => { Choice = choice; Close(); };
            return b;
        }
    }

    // ------------------------------------------------------------------------------
    //  (d) Command
    // ------------------------------------------------------------------------------
    [Transaction(TransactionMode.Manual)]
    public class DuctConnectorToDuctCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            MEPCurve duct;
            FamilyInstance fam;
            Connector conn;

            try
            {
                Reference pr = uidoc.Selection.PickObject(ObjectType.Element,
                    new DuctSelectionFilter(ElementId.InvalidElementId),
                    "Select the REFERENCE rigid duct (the element will move toward it)");
                duct = doc.GetElement(pr.ElementId) as MEPCurve;
                if (duct == null) return Result.Cancelled;

                Reference fr = uidoc.Selection.PickObject(ObjectType.Element, new DuctFamilyFilter(),
                    "Select the FAMILY to move toward the duct");
                fam = doc.GetElement(fr.ElementId) as FamilyInstance;
                if (fam == null) return Result.Cancelled;

                List<Connector> conns = ListConnectors(fam, duct);
                if (conns.Count == 0)
                {
                    TaskDialog.Show(DuctCfg.Caption, "The family has no usable HVAC connectors.");
                    return Result.Cancelled;
                }

                if (conns.Count == 1) conn = conns[0];
                else
                {
                    conn = DuctPick.ChooseConnector(uidoc, doc, fam, conns, duct.Id);
                    if (conn == null) return Result.Cancelled;
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            return MoveAndResolve(doc, duct.Id, fam.Id, conn.Id);
        }

        /// <summary>Palette entry: family + connector are already pinned, only the reference duct is asked.</summary>
        internal static Result ExecuteWithConnector(UIDocument uidoc, FamilyInstance fam, Connector conn, ref string message)
        {
            Document doc = uidoc.Document;
            MEPCurve duct;
            try
            {
                Reference pr = uidoc.Selection.PickObject(ObjectType.Element,
                    new DuctSelectionFilter(fam.Id),
                    "Select the REFERENCE rigid duct (the pinned element will move toward it)");
                duct = doc.GetElement(pr.ElementId) as MEPCurve;
                if (duct == null) return Result.Cancelled;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            return MoveAndResolve(doc, duct.Id, fam.Id, conn.Id);
        }

        private static Result MoveAndResolve(Document doc, ElementId ductId, ElementId famId, int connId)
        {
            // Different profile SHAPES block the workflow before anything moves
            string shapeError = DuctCtdEngine.CheckShape(doc, ductId, famId, connId);
            if (shapeError != null)
            {
                TaskDialog.Show(DuctCfg.Caption, shapeError);
                return Result.Cancelled;
            }

            var session = new DuctCtdSession { Doc = doc, DuctId = ductId, FamilyId = famId, ConnectorId = connId };

            string error;
            try { error = DuctCtdEngine.Run(session); }
            catch (Exception ex) { error = ex.Message; }

            if (error != null)
            {
                TaskDialog.Show(DuctCfg.Caption, error);
                return Result.Cancelled;
            }

            DuctCtdEngine.Finish(session);
            return Result.Succeeded;
        }

        /// <summary>HVAC connectors; if any share the duct profile shape, only those are offered.</summary>
        private static List<Connector> ListConnectors(FamilyInstance fam, MEPCurve duct)
        {
            List<Connector> all = DuctPick.ListConnectors(fam);
            DuctProfile dp = DuctProfiles.FromDuct(duct);
            List<Connector> match = all.Where(k => DuctProfiles.ShapeCompatible(DuctProfiles.FromConnector(k), dp)).ToList();
            return match.Count > 0 ? match : all;
        }
    }
}
