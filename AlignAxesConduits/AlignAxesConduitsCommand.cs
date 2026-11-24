using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace GestorVistas.AlignConduits
{
    [Transaction(TransactionMode.Manual)]
    public class AlignAxesConduitsCommand : IExternalCommand
    {
        private const string Title = "Align Axes Conduits";
        private const double LengthTol = 1e-6;        // feet
        private const double AngleTol = 1e-9;         // radians
        private const double MinConduitLength = 1e-3; // feet
        private const double FeetToMm = 304.8;

        // Gaps (feet) tried, in order, when a fitting (union / reducer) must be created.
        private static readonly double[] FittingGaps = { 0.1, 0.25, 0.5 };

        private const string LogoPath =
            @"C:\Users\gtv93\Desktop\Documents\17. Add-in\PipeMove\0.0 Distribution\Logos\AlignAxesconduit.png";

        // ------------------------------------------------------------------
        // Entry point
        // ------------------------------------------------------------------
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return ExecuteCommand(commandData.Application, ref message);
        }

        internal static Result ExecuteFromPalette(UIApplication application, ref string message)
        {
            return ExecuteCommand(application, ref message);
        }

        private static Result ExecuteCommand(UIApplication application, ref string message)
        {
            UIDocument uidoc = application.ActiveUIDocument;
            Document doc = uidoc.Document;
            View view = doc.ActiveView;
            ConduitSelectionFilter filter = new ConduitSelectionFilter();
            HalftoneIsolation isolation = null;

            Element pivot;
            try
            {
                pivot = PickConduit(uidoc, "Select the PIVOT conduit (reference axis)", filter);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            try
            {
                isolation = new HalftoneIsolation(doc, view, pivot.Id);
                isolation.Apply();
                uidoc.RefreshActiveView();

                Element second;
                while (true)
                {
                    second = PickConduit(uidoc, "Select the SECOND conduit to align", filter);
                    if (second.Id != pivot.Id) break;
                    ShowMessagePanel("Invalid selection",
                        "Please select a conduit different from the pivot.");
                }

                AlignSecondToPivot(doc, pivot, second);
                uidoc.RefreshActiveView();

                string action = ShowActionPanel();

                if (action == "connect")
                {
                    string warning = ConnectSecondToPivot(doc, pivot, second);
                    uidoc.RefreshActiveView();
                    if (!string.IsNullOrEmpty(warning))
                        ShowMessagePanel("Could not join the conduits", warning);
                }
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                ShowMessagePanel("The operation could not be completed", ex.Message);
                message = ex.Message;
                return Result.Failed;
            }
            finally
            {
                if (isolation != null)
                {
                    try
                    {
                        isolation.Restore();
                        uidoc.RefreshActiveView();
                    }
                    catch { }
                }
            }
        }

        // ------------------------------------------------------------------
        // Selection
        // ------------------------------------------------------------------
        private class ConduitSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element e)
            {
                return e != null && e.Category != null &&
                       e.Category.Id.Value == (long)BuiltInCategory.OST_Conduit;
            }

            public bool AllowReference(Autodesk.Revit.DB.Reference reference, XYZ position)
            {
                return false;
            }
        }

        private static Element PickConduit(UIDocument uidoc, string prompt, ISelectionFilter filter)
        {
            Autodesk.Revit.DB.Reference r = uidoc.Selection.PickObject(ObjectType.Element, filter, prompt);
            return uidoc.Document.GetElement(r.ElementId);
        }

        // ------------------------------------------------------------------
        // Transaction helpers
        // ------------------------------------------------------------------
        private static T RunTransaction<T>(Document doc, string name, Func<T> action)
        {
            using (Transaction t = new Transaction(doc, name))
            {
                t.Start();
                try
                {
                    T result = action();
                    t.Commit();
                    return result;
                }
                catch
                {
                    if (t.GetStatus() == TransactionStatus.Started)
                        t.RollBack();
                    throw;
                }
            }
        }

        private static void RunTransaction(Document doc, string name, Action action)
        {
            RunTransaction<object>(doc, name, () => { action(); return null; });
        }

        /// <summary>
        /// Runs an action inside a SubTransaction. Returns null on success, or the
        /// error message (after rolling back) on failure.
        /// </summary>
        private static string Attempt(Document doc, Action action)
        {
            using (SubTransaction st = new SubTransaction(doc))
            {
                st.Start();
                try
                {
                    action();
                    st.Commit();
                    return null;
                }
                catch (Exception ex)
                {
                    if (st.GetStatus() == TransactionStatus.Started)
                        st.RollBack();
                    string msg = ex.Message == null ? ex.GetType().Name : ex.Message.Trim();
                    return string.IsNullOrEmpty(msg) ? ex.GetType().Name : msg;
                }
            }
        }

        // ------------------------------------------------------------------
        // Halftone isolation
        // ------------------------------------------------------------------
        private class HalftoneIsolation
        {
            private readonly Document _doc;
            private readonly View _view;
            private readonly ElementId _keepId;
            private readonly List<KeyValuePair<ElementId, OverrideGraphicSettings>> _saved =
                new List<KeyValuePair<ElementId, OverrideGraphicSettings>>();

            public HalftoneIsolation(Document doc, View view, ElementId keepId)
            {
                _doc = doc;
                _view = view;
                _keepId = keepId;
            }

            public void Apply()
            {
                RunTransaction(_doc, Title + " - Apply Halftone", () =>
                {
                    FilteredElementCollector collector =
                        new FilteredElementCollector(_doc, _view.Id).WhereElementIsNotElementType();

                    foreach (Element el in collector)
                    {
                        if (el.Id == _keepId || el.Category == null) continue;
                        try
                        {
                            OverrideGraphicSettings original = _view.GetElementOverrides(el.Id);
                            if (original.Halftone) continue;

                            OverrideGraphicSettings modified = _view.GetElementOverrides(el.Id);
                            modified.SetHalftone(true);
                            _view.SetElementOverrides(el.Id, modified);
                            _saved.Add(new KeyValuePair<ElementId, OverrideGraphicSettings>(el.Id, original));
                        }
                        catch { }
                    }
                });
            }

            public void Restore()
            {
                if (_saved.Count == 0) return;
                try
                {
                    RunTransaction(_doc, Title + " - Restore Overrides", () =>
                    {
                        foreach (var item in _saved)
                        {
                            try
                            {
                                if (_doc.GetElement(item.Key) != null)
                                    _view.SetElementOverrides(item.Key, item.Value);
                            }
                            catch { }
                        }
                    });
                }
                finally
                {
                    _saved.Clear();
                }
            }
        }

        // ------------------------------------------------------------------
        // Geometry helpers
        // ------------------------------------------------------------------
        private static List<Connector> GetConnectors(Element element)
        {
            List<Connector> list = new List<Connector>();
            MEPCurve curve = element as MEPCurve;
            if (curve == null || curve.ConnectorManager == null) return list;
            foreach (Connector c in curve.ConnectorManager.Connectors)
                list.Add(c);
            return list;
        }

        private static Connector OtherConnector(List<Connector> connectors, Connector connector)
        {
            Connector best = null;
            double bestDist = -1.0;
            foreach (Connector c in connectors)
            {
                double dist = c.Origin.DistanceTo(connector.Origin);
                if (dist > bestDist) { best = c; bestDist = dist; }
            }
            return best;
        }

        private static void NearestPair(List<Connector> a, List<Connector> b,
                                        out Connector bestA, out Connector bestB)
        {
            bestA = null;
            bestB = null;
            double bestDist = double.MaxValue;
            foreach (Connector ca in a)
            {
                foreach (Connector cb in b)
                {
                    double dist = ca.Origin.DistanceTo(cb.Origin);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestA = ca;
                        bestB = cb;
                    }
                }
            }
        }

        private static Connector ClosestConnector(List<Connector> connectors, XYZ point)
        {
            Connector best = null;
            double bestDist = double.MaxValue;
            foreach (Connector c in connectors)
            {
                double dist = c.Origin.DistanceTo(point);
                if (dist < bestDist) { best = c; bestDist = dist; }
            }
            return best;
        }

        /// <summary>Connector diameter in feet, or -1 if it cannot be read.</summary>
        private static double DiameterOf(Connector c)
        {
            try { return c.Radius * 2.0; }
            catch { return -1.0; }
        }

        // ------------------------------------------------------------------
        // Alignment
        // ------------------------------------------------------------------
        private static void AlignSecondToPivot(Document doc, Element pivot, Element second)
        {
            List<Connector> pc = GetConnectors(pivot);
            List<Connector> sc = GetConnectors(second);
            if (pc.Count < 2 || sc.Count < 2)
                throw new InvalidOperationException("Both conduits must expose two connectors.");

            Connector cp, c2;
            NearestPair(pc, sc, out cp, out c2);
            Connector pivotFar = OtherConnector(pc, cp);
            Connector secondFar = OtherConnector(sc, c2);

            // Pivot axis direction, pointing outward through the facing end.
            XYZ d = cp.Origin.Subtract(pivotFar.Origin).Normalize();
            // Second conduit direction, pointing away from its facing end.
            XYZ d2 = secondFar.Origin.Subtract(c2.Origin).Normalize();

            // Choose the target direction that needs the smaller rotation.
            XYZ target = d2.DotProduct(d) >= 0 ? d : d.Negate();
            double angle = d2.AngleTo(target);
            XYZ anchor = c2.Origin;
            XYZ pivotPoint = cp.Origin;

            RunTransaction(doc, Title + " - Align Axes", () =>
            {
                if (angle > AngleTol)
                {
                    XYZ axis = d2.CrossProduct(target);
                    if (axis.GetLength() > 1e-12)
                    {
                        Line rotAxis = Line.CreateUnbound(anchor, axis.Normalize());
                        ElementTransformUtils.RotateElement(doc, second.Id, rotAxis, angle);
                    }
                }

                // Translate perpendicular to the axis so both axes coincide.
                double along = anchor.Subtract(pivotPoint).DotProduct(d);
                XYZ projected = pivotPoint.Add(d.Multiply(along));
                XYZ move = projected.Subtract(anchor);
                if (move.GetLength() > LengthTol)
                    ElementTransformUtils.MoveElement(doc, second.Id, move);

                doc.Regenerate();
            });
        }

        // ------------------------------------------------------------------
        // Join
        //   1) Merge: the pivot is extended to the far end of the second and the
        //      second is removed -> ONE single conduit (same as dragging the
        //      end grip by hand).
        //   2) Fallback: stretch + direct connection / union / reducer.
        // Returns null on success, or a message explaining why it failed.
        // On failure the conduits stay aligned and stretched (Ctrl+Z to undo).
        // ------------------------------------------------------------------
        private static string ConnectSecondToPivot(Document doc, Element pivot, Element second)
        {
            ElementId pivotId = pivot.Id;
            ElementId secondId = second.Id;

            List<Connector> pc = GetConnectors(pivot);
            List<Connector> sc = GetConnectors(second);

            Connector cp, c2;
            NearestPair(pc, sc, out cp, out c2);

            if (cp.IsConnected)
                return "The facing connector of the pivot conduit is already connected to another element.";
            if (c2.IsConnected)
                return "The facing connector of the second conduit is already connected to another element.";

            return RunTransaction<string>(doc, Title + " - Join", () =>
            {
                List<string> reasons = new List<string>();

                if (TryMerge(doc, pivotId, secondId, reasons))
                    return null;

                // Merge failed and was rolled back: continue with the fallback.
                Element p = doc.GetElement(pivotId);
                Element s = doc.GetElement(secondId);
                return StretchAndConnect(doc, p, s, reasons);
            });
        }

        /// <summary>
        /// Extends the pivot to the far end of the second conduit and deletes the second.
        /// Anything attached to the far end of the second is re-attached to the pivot.
        /// </summary>
        private static bool TryMerge(Document doc, ElementId pivotId, ElementId secondId,
                                     List<string> reasons)
        {
            string error = Attempt(doc, () =>
            {
                Element pivot = doc.GetElement(pivotId);
                Element second = doc.GetElement(secondId);

                if (pivot.GetTypeId() != second.GetTypeId())
                    throw new InvalidOperationException(
                        "the two conduits have different conduit types.");

                List<Connector> pc = GetConnectors(pivot);
                List<Connector> sc = GetConnectors(second);

                Connector cp, c2;
                NearestPair(pc, sc, out cp, out c2);

                double dPivot = DiameterOf(cp);
                double dSecond = DiameterOf(c2);
                if (dPivot <= 0 || dSecond <= 0 || Math.Abs(dPivot - dSecond) > LengthTol)
                    throw new InvalidOperationException(
                        "the two conduits have different diameters.");

                Connector pivotFar = OtherConnector(pc, cp);
                Connector secondFar = OtherConnector(sc, c2);

                LocationCurve loc = pivot.Location as LocationCurve;
                if (loc == null)
                    throw new InvalidOperationException("the pivot conduit has no location curve.");

                Curve curve = loc.Curve;
                XYZ p0 = curve.GetEndPoint(0);
                XYZ p1 = curve.GetEndPoint(1);
                bool facingIsStart = p0.DistanceTo(cp.Origin) <= p1.DistanceTo(cp.Origin);
                XYZ fixedPt = facingIsStart ? p1 : p0;
                XYZ newEnd = secondFar.Origin;

                XYZ dir = cp.Origin.Subtract(pivotFar.Origin).Normalize();
                if (newEnd.Subtract(fixedPt).DotProduct(dir) < MinConduitLength)
                    throw new InvalidOperationException(
                        "the merged conduit would be too short or reversed.");

                // Elements attached to the far end of the second conduit.
                List<Connector> farRefs = new List<Connector>();
                foreach (Connector r in secondFar.AllRefs)
                {
                    if (r.Owner != null && r.Owner.Id != secondId && r.Owner.Id != pivotId)
                        farRefs.Add(r);
                }

                // The second conduit is absorbed by the pivot.
                doc.Delete(secondId);

                loc.Curve = facingIsStart
                    ? Line.CreateBound(newEnd, fixedPt)
                    : Line.CreateBound(fixedPt, newEnd);
                doc.Regenerate();

                // Restore whatever was attached at the far end.
                if (farRefs.Count > 0)
                {
                    Connector newC = ClosestConnector(GetConnectors(pivot), newEnd);
                    foreach (Connector r in farRefs)
                    {
                        if (!newC.IsConnectedTo(r))
                            newC.ConnectTo(r);
                    }
                }
            });

            if (error == null) return true;
            reasons.Add("Merge into a single conduit: " + error);
            return false;
        }

        /// <summary>Stretch to touch, then direct connection / union / reducer.</summary>
        private static string StretchAndConnect(Document doc, Element pivot, Element second,
                                                List<string> reasons)
        {
            List<Connector> pc = GetConnectors(pivot);
            List<Connector> sc = GetConnectors(second);

            Connector cp, c2;
            NearestPair(pc, sc, out cp, out c2);

            LocationCurve loc = second.Location as LocationCurve;
            if (loc == null)
                return "The second conduit has no location curve.";

            Curve curve = loc.Curve;
            XYZ p0 = curve.GetEndPoint(0);
            XYZ p1 = curve.GetEndPoint(1);
            bool facingIsStart = p0.DistanceTo(c2.Origin) <= p1.DistanceTo(c2.Origin);
            XYZ fixedPt = facingIsStart ? p1 : p0;
            XYZ oldDir = fixedPt.Subtract(c2.Origin).Normalize();
            XYZ pivotEnd = cp.Origin;

            XYZ toFixed = fixedPt.Subtract(pivotEnd);
            if (toFixed.GetLength() < MinConduitLength || toFixed.DotProduct(oldDir) <= 0)
                return "Cannot stretch: the resulting conduit would be too short or reversed.";

            Func<XYZ, Line> buildLine = end => facingIsStart
                ? Line.CreateBound(end, fixedPt)
                : Line.CreateBound(fixedPt, end);

            // Step 1: stretch the facing end onto the pivot connector.
            loc.Curve = buildLine(pivotEnd);
            doc.Regenerate();

            Connector cp2 = ClosestConnector(GetConnectors(pivot), pivotEnd);
            Connector c22 = ClosestConnector(GetConnectors(second), pivotEnd);

            double dPivot = DiameterOf(cp2);
            double dSecond = DiameterOf(c22);
            bool sameSize = dPivot > 0 && dSecond > 0 &&
                            Math.Abs(dPivot - dSecond) <= LengthTol;

            if (sameSize)
            {
                if (TryDirect(doc, pivot, second, pivotEnd, reasons))
                    return null;
                if (TryFittings(doc, pivot, second, facingIsStart, fixedPt, pivotEnd, true, reasons))
                    return null;
            }
            else
            {
                if (TryFittings(doc, pivot, second, facingIsStart, fixedPt, pivotEnd, false, reasons))
                    return null;
                if (TryDirect(doc, pivot, second, pivotEnd, reasons))
                    return null;
            }

            return BuildFailureMessage(dPivot, dSecond, sameSize, reasons);
        }

        /// <summary>Direct connector-to-connector connection at the touching point.</summary>
        private static bool TryDirect(Document doc, Element pivot, Element second,
                                      XYZ pivotEnd, List<string> reasons)
        {
            string error = Attempt(doc, () =>
            {
                Connector a = ClosestConnector(GetConnectors(pivot), pivotEnd);
                Connector b = ClosestConnector(GetConnectors(second), pivotEnd);

                double gap = a.Origin.DistanceTo(b.Origin);
                if (gap > LengthTol)
                    throw new InvalidOperationException(
                        "the connectors are not coincident (gap = " + (gap * FeetToMm).ToString("0.###") + " mm).");

                b.ConnectTo(a);

                if (!a.IsConnectedTo(b))
                    throw new InvalidOperationException(
                        "ConnectTo completed but the connectors are not connected.");
            });

            if (error == null) return true;
            reasons.Add("Direct connector-to-connector: " + error);
            return false;
        }

        /// <summary>
        /// Opens a small gap and places a union (same size) or a reducer (different size).
        /// Tries increasing gaps until one works.
        /// </summary>
        private static bool TryFittings(Document doc, Element pivot, Element second,
                                        bool facingIsStart, XYZ fixedPt, XYZ pivotEnd,
                                        bool sameSize, List<string> reasons)
        {
            string kind = sameSize ? "Union fitting" : "Reducer (transition fitting)";

            foreach (double gapLen in FittingGaps)
            {
                double g = gapLen;
                string error = Attempt(doc, () =>
                {
                    XYZ dirAway = fixedPt.Subtract(pivotEnd).Normalize();
                    XYZ gapEnd = pivotEnd.Add(dirAway.Multiply(g));
                    if (fixedPt.DistanceTo(gapEnd) < MinConduitLength)
                        throw new InvalidOperationException(
                            "the second conduit is too short to leave room for a fitting.");

                    LocationCurve l = second.Location as LocationCurve;
                    l.Curve = facingIsStart
                        ? Line.CreateBound(gapEnd, fixedPt)
                        : Line.CreateBound(fixedPt, gapEnd);
                    doc.Regenerate();

                    Connector a = ClosestConnector(GetConnectors(pivot), pivotEnd);
                    Connector b = ClosestConnector(GetConnectors(second), gapEnd);

                    FamilyInstance fitting = sameSize
                        ? doc.Create.NewUnionFitting(a, b)
                        : doc.Create.NewTransitionFitting(a, b);

                    if (fitting == null)
                        throw new InvalidOperationException("Revit did not return a fitting.");
                });

                if (error == null) return true;
                reasons.Add(kind + " (gap " + g.ToString("0.00") + " ft): " + error);
            }
            return false;
        }

        private static string BuildFailureMessage(double dPivot, double dSecond,
                                                  bool sameSize, List<string> reasons)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("The conduits were aligned and stretched until they touch, " +
                          "but Revit could not join them.");
            sb.AppendLine("You can close this window and keep the result, or press Ctrl+Z to undo it.");
            sb.AppendLine();

            string pivotD = dPivot > 0 ? (dPivot * FeetToMm).ToString("0.##") + " mm" : "unknown";
            string secondD = dSecond > 0 ? (dSecond * FeetToMm).ToString("0.##") + " mm" : "unknown";
            sb.AppendLine("Diameters: pivot = " + pivotD + ", second = " + secondD +
                          (sameSize ? " (same size)." : " (different size)."));
            sb.AppendLine();

            sb.AppendLine("What was tried:");
            foreach (string r in reasons)
                sb.AppendLine(" - " + r);
            sb.AppendLine();

            sb.AppendLine("Possible causes:");
            if (!sameSize)
                sb.AppendLine(" - The conduit type's Routing Preferences have no transition (reducer) " +
                              "fitting for these sizes.");
            else
                sb.AppendLine(" - The conduit type's Routing Preferences have no union fitting.");
            sb.AppendLine(" - The conduit types or conduit kinds (with / without fittings) are not compatible.");
            sb.AppendLine(" - The conduits are not perfectly collinear or are too short to fit a fitting.");

            return sb.ToString().TrimEnd();
        }

        // ------------------------------------------------------------------
        // Shared window helpers
        // ------------------------------------------------------------------
        private static BitmapImage LoadLogo()
        {
            try
            {
                if (!File.Exists(LogoPath)) return null;
                BitmapImage bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(LogoPath, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static void ApplyLogo(System.Windows.Window win)
        {
            BitmapImage logo = LoadLogo();
            System.Windows.Controls.Image img =
                (System.Windows.Controls.Image)win.FindName("LogoImage");
            if (logo != null)
            {
                img.Source = logo;
                win.Icon = logo;
            }
            else
            {
                img.Visibility = System.Windows.Visibility.Collapsed;
            }
        }

        private static void SetOwner(System.Windows.Window win)
        {
            try
            {
                new WindowInteropHelper(win).Owner = Process.GetCurrentProcess().MainWindowHandle;
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // Message window (same dark style as the action panel)
        // ------------------------------------------------------------------
        private const string MessageXaml = @"
<Window xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
        WindowStyle=""None"" AllowsTransparency=""True"" Background=""Transparent""
        ResizeMode=""NoResize"" SizeToContent=""Height"" Width=""460"" Topmost=""True""
        ShowInTaskbar=""False"" WindowStartupLocation=""CenterScreen""
        FontFamily=""Segoe UI"">
  <Window.Resources>
    <Style x:Key=""FlatBtn"" TargetType=""Button"">
      <Setter Property=""Background"" Value=""Transparent""/>
      <Setter Property=""Foreground"" Value=""#D0D6DD""/>
      <Setter Property=""BorderThickness"" Value=""0""/>
      <Setter Property=""Focusable"" Value=""False""/>
      <Setter Property=""Cursor"" Value=""Hand""/>
      <Setter Property=""Template"">
        <Setter.Value>
          <ControlTemplate TargetType=""Button"">
            <Border x:Name=""Bd"" Background=""{TemplateBinding Background}"" CornerRadius=""4"">
              <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property=""IsMouseOver"" Value=""True"">
                <Setter TargetName=""Bd"" Property=""Background"" Value=""#38414B""/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </Window.Resources>
  <Border Background=""#1E2227"" BorderBrush=""#3A424C"" BorderThickness=""1"" CornerRadius=""6"">
    <Grid>
      <Grid.RowDefinitions>
        <RowDefinition Height=""28""/>
        <RowDefinition Height=""Auto""/>
        <RowDefinition Height=""Auto""/>
      </Grid.RowDefinitions>

      <Border x:Name=""Header"" Grid.Row=""0"" Background=""#2B3139"" CornerRadius=""5,5,0,0"">
        <Grid>
          <StackPanel Orientation=""Horizontal"" VerticalAlignment=""Center"" Margin=""8,0,0,0"">
            <Image x:Name=""LogoImage"" Width=""16"" Height=""16"" Margin=""0,0,6,0""
                   RenderOptions.BitmapScalingMode=""HighQuality""/>
            <TextBlock x:Name=""TitleText"" Text=""Align Axes Conduits"" Foreground=""#E6EAEE""
                       FontSize=""11"" FontWeight=""SemiBold"" VerticalAlignment=""Center""/>
          </StackPanel>
          <Button x:Name=""CloseButton"" Style=""{StaticResource FlatBtn}"" Content=""&#x2715;""
                  Width=""28"" Height=""22"" HorizontalAlignment=""Right"" Margin=""0,0,3,0""
                  FontSize=""11""/>
        </Grid>
      </Border>

      <StackPanel Grid.Row=""1"" Margin=""14,12,14,4"">
        <TextBlock x:Name=""HeadingText"" Foreground=""#E6EAEE"" FontSize=""13""
                   FontWeight=""SemiBold"" TextWrapping=""Wrap""/>
        <TextBox x:Name=""BodyText"" IsReadOnly=""True"" TextWrapping=""Wrap""
                 Background=""Transparent"" BorderThickness=""0"" Foreground=""#B8C0C9""
                 FontSize=""11"" Margin=""0,8,0,0"" MaxHeight=""300""
                 VerticalScrollBarVisibility=""Auto"" Padding=""0""
                 CaretBrush=""#B8C0C9"" SelectionBrush=""#4A5563""/>
      </StackPanel>

      <Button x:Name=""OkButton"" Grid.Row=""2"" Style=""{StaticResource FlatBtn}""
              Background=""#272D34"" Width=""110"" Height=""30"" Margin=""14,8,14,12""
              HorizontalAlignment=""Right"">
        <StackPanel Orientation=""Horizontal"">
          <TextBlock Text=""Close"" Foreground=""#E6EAEE"" FontWeight=""SemiBold"" FontSize=""11""/>
          <TextBlock Text=""  Esc"" Foreground=""#7C8793"" FontSize=""9"" VerticalAlignment=""Center""/>
        </StackPanel>
      </Button>
    </Grid>
  </Border>
</Window>";

        private static void ShowMessagePanel(string heading, string body)
        {
            System.Windows.Window win = (System.Windows.Window)XamlReader.Parse(MessageXaml);

            ((System.Windows.Controls.TextBlock)win.FindName("HeadingText")).Text = heading;
            ((System.Windows.Controls.TextBox)win.FindName("BodyText")).Text = body;

            ApplyLogo(win);
            SetOwner(win);

            bool closed = false;
            Action close = () =>
            {
                if (closed) return;
                closed = true;
                win.Close();
            };

            ((System.Windows.Controls.Button)win.FindName("OkButton")).Click += (s, e) => close();
            ((System.Windows.Controls.Button)win.FindName("CloseButton")).Click += (s, e) => close();

            ((System.Windows.Controls.Border)win.FindName("Header")).MouseLeftButtonDown +=
                (s, e) =>
                {
                    try { win.DragMove(); } catch { }
                };

            win.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape ||
                    e.Key == System.Windows.Input.Key.Enter)
                {
                    e.Handled = true;
                    close();
                }
            };

            win.Loaded += (s, e) => win.Activate();
            win.ShowDialog();
        }

        // ------------------------------------------------------------------
        // Compact floating dark panel
        // ------------------------------------------------------------------
        private const string PanelXaml = @"
<Window xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
        WindowStyle=""None"" AllowsTransparency=""True"" Background=""Transparent""
        ResizeMode=""NoResize"" SizeToContent=""WidthAndHeight"" Topmost=""True""
        ShowInTaskbar=""False"" WindowStartupLocation=""CenterScreen""
        FontFamily=""Segoe UI"">
  <Window.Resources>
    <Style x:Key=""FlatBtn"" TargetType=""Button"">
      <Setter Property=""Background"" Value=""Transparent""/>
      <Setter Property=""Foreground"" Value=""#D0D6DD""/>
      <Setter Property=""BorderThickness"" Value=""0""/>
      <Setter Property=""Focusable"" Value=""False""/>
      <Setter Property=""Cursor"" Value=""Hand""/>
      <Setter Property=""Template"">
        <Setter.Value>
          <ControlTemplate TargetType=""Button"">
            <Border x:Name=""Bd"" Background=""{TemplateBinding Background}"" CornerRadius=""4"">
              <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property=""IsMouseOver"" Value=""True"">
                <Setter TargetName=""Bd"" Property=""Background"" Value=""#38414B""/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </Window.Resources>
  <Border Background=""#1E2227"" BorderBrush=""#3A424C"" BorderThickness=""1""
          CornerRadius=""6"" Width=""230"">
    <Grid>
      <Grid.RowDefinitions>
        <RowDefinition Height=""28""/>
        <RowDefinition Height=""Auto""/>
      </Grid.RowDefinitions>

      <Border x:Name=""Header"" Grid.Row=""0"" Background=""#2B3139"" CornerRadius=""5,5,0,0"">
        <Grid>
          <StackPanel Orientation=""Horizontal"" VerticalAlignment=""Center"" Margin=""8,0,0,0"">
            <Image x:Name=""LogoImage"" Width=""16"" Height=""16"" Margin=""0,0,6,0""
                   RenderOptions.BitmapScalingMode=""HighQuality""/>
            <TextBlock Text=""Align Axes Conduits"" Foreground=""#E6EAEE"" FontSize=""11""
                       FontWeight=""SemiBold"" VerticalAlignment=""Center""/>
          </StackPanel>
          <Button x:Name=""CloseButton"" Style=""{StaticResource FlatBtn}"" Content=""&#x2715;""
                  Width=""28"" Height=""22"" HorizontalAlignment=""Right"" Margin=""0,0,3,0""
                  FontSize=""11""/>
        </Grid>
      </Border>

      <Grid Grid.Row=""1"" Margin=""6"">
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width=""*""/>
          <ColumnDefinition Width=""*""/>
        </Grid.ColumnDefinitions>

        <Button x:Name=""ConnectButton"" Grid.Column=""0"" Style=""{StaticResource FlatBtn}""
                Background=""#272D34"" Margin=""3"" Height=""64"">
          <StackPanel>
            <Viewbox Width=""22"" Height=""22"">
              <Canvas Width=""24"" Height=""24"">
                <Path Data=""M2,12 H9 M9,6 V18 M22,12 H15 M15,6 V18 M10.5,12 H13.5""
                      Stroke=""#4CC38A"" StrokeThickness=""2"" StrokeStartLineCap=""Round""
                      StrokeEndLineCap=""Round""/>
              </Canvas>
            </Viewbox>
            <TextBlock Text=""Connect"" Foreground=""#E6EAEE"" FontWeight=""SemiBold""
                       FontSize=""11"" HorizontalAlignment=""Center"" Margin=""0,3,0,0""/>
            <TextBlock Text=""Enter / Space"" Foreground=""#7C8793"" FontSize=""8.5""
                       HorizontalAlignment=""Center""/>
          </StackPanel>
        </Button>

        <Button x:Name=""LeaveButton"" Grid.Column=""1"" Style=""{StaticResource FlatBtn}""
                Background=""#272D34"" Margin=""3"" Height=""64"">
          <StackPanel>
            <Viewbox Width=""22"" Height=""22"">
              <Canvas Width=""24"" Height=""24"">
                <Ellipse Canvas.Left=""2"" Canvas.Top=""2"" Width=""20"" Height=""20""
                         Stroke=""#E5484D"" StrokeThickness=""2""/>
                <Line X1=""5.5"" Y1=""5.5"" X2=""18.5"" Y2=""18.5"" Stroke=""#E5484D""
                      StrokeThickness=""2"" StrokeStartLineCap=""Round""
                      StrokeEndLineCap=""Round""/>
              </Canvas>
            </Viewbox>
            <TextBlock Text=""Leave"" Foreground=""#E6EAEE"" FontWeight=""SemiBold""
                       FontSize=""11"" HorizontalAlignment=""Center"" Margin=""0,3,0,0""/>
            <TextBlock Text=""Esc"" Foreground=""#7C8793"" FontSize=""8.5""
                       HorizontalAlignment=""Center""/>
          </StackPanel>
        </Button>
      </Grid>
    </Grid>
  </Border>
</Window>";

        /// <summary>Shows the panel and returns "connect" or "leave".</summary>
        private static string ShowActionPanel()
        {
            System.Windows.Window win = (System.Windows.Window)XamlReader.Parse(PanelXaml);
            string action = "leave";
            bool done = false;
            bool active = false;

            ApplyLogo(win);
            SetOwner(win);

            Action<string> finish = result =>
            {
                if (done) return;
                done = true;
                action = result;
                win.Close();
            };

            ((System.Windows.Controls.Button)win.FindName("ConnectButton")).Click +=
                (s, e) => finish("connect");
            ((System.Windows.Controls.Button)win.FindName("LeaveButton")).Click +=
                (s, e) => finish("leave");
            ((System.Windows.Controls.Button)win.FindName("CloseButton")).Click +=
                (s, e) => finish("leave");

            ((System.Windows.Controls.Border)win.FindName("Header")).MouseLeftButtonDown +=
                (s, e) =>
                {
                    try { win.DragMove(); } catch { }
                };

            win.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter ||
                    e.Key == System.Windows.Input.Key.Space)
                {
                    e.Handled = true;
                    finish("connect");
                }
                else if (e.Key == System.Windows.Input.Key.Escape)
                {
                    e.Handled = true;
                    finish("leave");
                }
            };

            win.Activated += (s, e) => active = true;
            // Losing focus (clicking outside) leaves the conduits aligned.
            win.Deactivated += (s, e) =>
            {
                if (active) finish("leave");
            };
            win.Loaded += (s, e) => win.Activate();

            win.ShowDialog();
            return action;
        }
    }
}