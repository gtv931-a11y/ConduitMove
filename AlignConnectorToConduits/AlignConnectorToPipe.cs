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
//    4) The family moves in plan (X, Y; Z is not modified) until the connector is
//       aligned with the curve. Same engine as "Align To Connector":
//         - Attached pipes/conduits stretch or move as a whole (unbounded).
//         - If the chain touches ANOTHER family, or the move collides with another element,
//           the conflict window opens (disconnect / move with element / focus).
//    5) If the connector diameter does not match the curve diameter, a palette
//       (RGB 59, 68, 83) appears with two options: change the curve or change the connector.
//
//  Reuses internal classes from aligntoconnector.cs (namespace AlignToConnector):
//  Cfg, AlignSession, AlignWindow, ConflictInfo, FrontierInfo, DominoRisk, ChainJoint,
//  KeyConfirmHook, ConnectorMarkerServer, ConnectorFamilyFilter, MepCurveSelectionFilter,
//  RollbackOnErrorPreprocessor and AlignEngine (public methods).
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
        public bool ForceRigid;         // retry: move all neighboring segments as a whole
        public string Note;
        public Dictionary<ElementId, FrontierPolicy> Policies = new Dictionary<ElementId, FrontierPolicy>();

        /// <summary>Visual state shared with AlignWindow / ClearVisuals.</summary>
        public AlignSession Vis;
    }

    internal sealed class CtpPlan
    {
        public FamilyInstance Family;
        public XYZ Displacement = XYZ.Zero;
        public bool AlreadyAligned;
        public HashSet<ElementId> MoverIds = new HashSet<ElementId>();
        public HashSet<ElementId> ChainIds = new HashSet<ElementId>();
        public HashSet<ElementId> FrontierIds = new HashSet<ElementId>();
        public List<ChainJoint> Joints = new List<ChainJoint>();
        public List<FrontierInfo> Frontiers = new List<FrontierInfo>();
        public ConflictInfo Conflict;

        public ConflictInfo Fail(ConflictKind kind, XYZ point, ElementId element, string msg)
        {
            Conflict = new ConflictInfo
            {
                Kind = kind,
                Point = point ?? XYZ.Zero,
                CurveId = Family != null ? Family.Id : ElementId.InvalidElementId,
                ConflictElementId = element ?? ElementId.InvalidElementId,
                ChainIds = ChainIds,
                Message = msg
            };
            return Conflict;
        }
    }

    // ------------------------------------------------------------------------------
    //  Engine
    // ------------------------------------------------------------------------------
    internal static class CtpEngine
    {
        private enum NbResult { Ok, NoGeometry, Skew, NoSlack }

        // ------------------------- Utilities -------------------------

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

        /// <summary>Segments that stretch or move as a whole together with the family.</summary>
        private static bool IsChainCurve(Element e)
        {
            return e is Pipe || e is Conduit;
        }

        /// <summary>Fittings (elbows, tees, unions): move with the family. Junction boxes act as a frontier.</summary>
        private static bool IsFitting(Element e)
        {
            FamilyInstance fi = e as FamilyInstance;
            if (fi == null || e.Category == null) return false;

            bool inCategory = false;
            foreach (BuiltInCategory bic in Cfg.FittingCategories)
                if (e.Category.Id.Equals(new ElementId(bic))) { inCategory = true; break; }
            if (!inCategory) return false;

            Autodesk.Revit.DB.Mechanical.MechanicalFitting mf = fi.MEPModel as Autodesk.Revit.DB.Mechanical.MechanicalFitting;
            if (mf != null && mf.PartType.ToString() == "JunctionBox") return false;

            return true;
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

        internal static Connector FindConnector(Document doc, ElementId ownerId, int connId)
        {
            foreach (Connector c in AlignEngine.GetConnectors(doc.GetElement(ownerId)))
                if (c.Id == connId) return c;
            return null;
        }

        // ------------------------- Main Flow -------------------------

        /// <summary>Evaluates and applies. Returns true if aligned; false if there is a conflict (highlighted).</summary>
        public static bool Run(CtpSession s, UIDocument uidoc)
        {
            s.Vis.Conflict = null;
            s.Note = null;

            CtpPlan r = Evaluate(s);

            if (r.AlreadyAligned)
            {
                s.Note = "The element was not moved: in plan view (X, Y) the connector is already on the curve axis " +
                         "(difference " + (r.Displacement.GetLength() / Cfg.MmToFt).ToString("0.##") + " mm).\n" +
                         "This command only aligns in plan view and does not change Z elevation.";
                return true;
            }

            if (r.Conflict == null)
            {
                r.Conflict = ApplyAlignment(s, r);

                // If Revit rejects the adjustment, retry moving neighboring segments as a whole without stretching
                if (r.Conflict != null && r.Conflict.Kind == ConflictKind.RevitRejected)
                {
                    s.ForceRigid = true;
                    try
                    {
                        CtpPlan r2 = Evaluate(s);
                        if (!r2.AlreadyAligned)
                        {
                            if (r2.Conflict == null) r2.Conflict = ApplyAlignment(s, r2);
                            r = r2;
                        }
                    }
                    finally { s.ForceRigid = false; }
                }
            }

            if (r.Conflict != null)
            {
                s.Vis.Conflict = r.Conflict;
                HighlightConflict(s, uidoc);
                return false;
            }
            return true;
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
        /// Plan-only displacement needed for the FAMILY so its connector lands on the curve axis:
        /// perpendicular to the axis in plan view. If the curve is vertical, moves to its XY.
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
            XYZ onAxis = axis.Evaluate(0.5, true);
            XYZ v = new XYZ(onAxis.X - o.X, onAxis.Y - o.Y, 0);

            XYZ uh = new XYZ(axis.Direction.X, axis.Direction.Y, 0);
            if (uh.GetLength() < 1e-3) return v;   // vertical curve

            uh = uh.Normalize();
            XYZ n = new XYZ(-uh.Y, uh.X, 0);
            return n.Multiply(v.DotProduct(n));
        }

        // ------------------------- Chain Evaluation -------------------------

        private static CtpPlan Evaluate(CtpSession s)
        {
            Document doc = s.Doc;
            FamilyInstance fam = doc.GetElement(s.FamilyId) as FamilyInstance;
            MEPCurve pipe = doc.GetElement(s.PipeId) as MEPCurve;

            var r = new CtpPlan { Family = fam };
            if (fam == null || pipe == null)
            {
                r.Fail(ConflictKind.InvalidGeometry, XYZ.Zero, ElementId.InvalidElementId,
                    "The element or reference pipe/conduit no longer exists in the model.");
                return r;
            }
            r.ChainIds.Add(fam.Id);

            if (fam.Pinned)
            {
                r.Fail(ConflictKind.InvalidGeometry, CenterOf(fam), fam.Id,
                    "The element is pinned. Unpin it before moving it.");
                return r;
            }

            string err;
            XYZ d = ComputeDisplacement(s, out err);
            if (err != null)
            {
                r.Fail(ConflictKind.InvalidGeometry, CenterOf(fam), fam.Id, err);
                return r;
            }
            r.Displacement = d;
            if (d.GetLength() < Cfg.AlignTolFt) { r.AlreadyAligned = true; return r; }

            // ===== Pass 1: main chain through connectors =====
            var movers = new HashSet<ElementId> { fam.Id };
            var queue = new Queue<Element>();
            queue.Enqueue(fam);

            var neighbours = new Dictionary<ElementId, List<XYZ>>();
            var frontierSeen = new HashSet<ElementId>();

            while (queue.Count > 0)
            {
                Element cur = queue.Dequeue();
                foreach (Connector c in AlignEngine.GetConnectors(cur))
                {
                    foreach (Connector other in GetRefs(c, cur))
                    {
                        Element o = other.Owner;
                        if (o == null || movers.Contains(o.Id)) continue;

                        if (o.Id.Equals(pipe.Id))
                        {
                            return FailPipeInChain(r, other.Origin, pipe);
                        }

                        if (IsFitting(o))
                        {
                            movers.Add(o.Id);
                            r.ChainIds.Add(o.Id);
                            queue.Enqueue(o);
                        }
                        else if (IsChainCurve(o))
                        {
                            // If the neighbor cannot absorb the move (skewed / too short) it is NOT a conflict:
                            // the segment moves as a whole with the chain.
                            XYZ nbNear;
                            double nbAmt;
                            if (s.ForceRigid || CheckNeighbour((MEPCurve)o, other.Origin, d, out nbNear, out nbAmt) != NbResult.Ok)
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
                        else if (frontierSeen.Add(o.Id))
                        {
                            r.Frontiers.Add(new FrontierInfo { Family = o, ChainSide = c, FamilySide = other });
                            r.FrontierIds.Add(o.Id);
                        }
                    }
                }
            }
            foreach (ElementId moverId in movers) r.MoverIds.Add(moverId);

            // ===== Pass 2: frontier families + domino effect =====
            foreach (FrontierInfo fi in r.Frontiers)
            {
                AnalyzeDomino(d, fi, r.ChainIds);

                if (s.Policies.ContainsKey(fi.Family.Id)) continue;   // already decided by user

                ConflictInfo cf = r.Fail(ConflictKind.Frontier, fi.FamilySide.Origin, fi.Family.Id,
                    "The element chain touches another family (frontier). Choose 'Disconnect & Align' " +
                    "(moves only the element and detaches that connection) or 'Move with Element' " +
                    "(moves the other family along with it).");
                cf.RiskCount = fi.Risks.Count;
                cf.RiskText = string.Join("\n", fi.Risks.Select(k =>
                    "• " + AlignEngine.Describe(doc, k.Blocker == null ? ElementId.InvalidElementId : k.Blocker.Id) +
                    " — " + k.Reason));
                return r;
            }

            // ===== Fuses: neighboring straight segments =====
            foreach (var kv in neighbours)
            {
                MEPCurve q = doc.GetElement(kv.Key) as MEPCurve;
                if (q == null) continue;
                r.ChainIds.Add(q.Id);

                if (kv.Value.Count > 1) continue;   // both ends move: translated only

                XYZ near;
                double amount;
                NbResult nb = CheckNeighbour(q, kv.Value[0], d, out near, out amount);
                if (nb == NbResult.Ok) continue;

                if (nb == NbResult.NoGeometry)
                    r.Fail(ConflictKind.InvalidGeometry, near, q.Id, "A neighboring segment does not have a valid linear geometry.");
                else if (nb == NbResult.Skew)
                    r.Fail(ConflictKind.Skew, near, q.Id,
                        "The neighboring segment is not parallel to the displacement: it would skew (" +
                        (amount / Cfg.MmToFt).ToString("0.#") + " mm lateral deviation).");
                else
                    r.Fail(ConflictKind.NoSlack, near, q.Id,
                        "The neighboring segment cannot absorb the displacement: short by " +
                        (amount / Cfg.MmToFt).ToString("0.#") + " mm.");
                return r;
            }

            // ===== Pass 3: physical collision =====
            CheckCollisions(s, r, d);
            return r;
        }

        private static CtpPlan FailPipeInChain(CtpPlan r, XYZ point, MEPCurve pipe)
        {
            r.Fail(ConflictKind.InvalidGeometry, point, pipe.Id,
                "The reference pipe/conduit is connected to the element chain: moving it would drag it. " +
                "Pick another reference pipe/conduit or disconnect it first.");
            return r;
        }

        /// <summary>Neighboring segment keeps its far end fixed; the near end moves with the displacement.</summary>
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

        // ------------------------- Domino Effect -------------------------

        private static void AnalyzeDomino(XYZ d, FrontierInfo fi, HashSet<ElementId> chainIds)
        {
            var visited = new HashSet<ElementId>(chainIds) { fi.Family.Id };

            foreach (Connector fc in AlignEngine.GetConnectors(fi.Family))
            {
                if (fc.Id == fi.FamilySide.Id) continue;   // connection with our chain

                foreach (Connector first in GetRefs(fc, fi.Family))
                {
                    if (first.Owner == null || visited.Contains(first.Owner.Id)) continue;
                    DominoRisk risk = TraceBranch(d, fc, first, visited);
                    if (risk != null) fi.Risks.Add(risk);
                }
            }
        }

        private static DominoRisk TraceBranch(XYZ d, Connector fc, Connector first, HashSet<ElementId> visited)
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
                    foreach (Connector c in AlignEngine.GetConnectors(o))
                    {
                        if (c.Id == entry.Id) continue;
                        foreach (Connector other in GetRefs(c, o))
                            queue.Enqueue(new KeyValuePair<Element, Connector>(other.Owner, other));
                    }
                }
                else if (IsChainCurve(o))
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
                                ? "the segment cannot absorb the movement without skewing"
                                : "the segment is too short to absorb the movement"
                        };
                }
                else
                {
                    return new DominoRisk
                    {
                        OnFamily = fc,
                        FirstOther = first,
                        Blocker = o,
                        Point = entry.Origin,
                        Reason = "connected family that would be dragged"
                    };
                }
            }
            return null;
        }

        // ------------------------- Collision (family body sweep) -------------------------

        private static ICollection<ElementId> HitsOf(Document doc, Solid solid, XYZ oMin, XYZ oMax)
        {
            return new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory>(Cfg.CollisionCategories)))
                .WherePasses(new BoundingBoxIntersectsFilter(new Outline(oMin, oMax)))
                .WherePasses(new ElementIntersectsSolidFilter(solid))
                .ToElementIds();
        }

        private static Solid Extrude(XYZ p0, XYZ p1, XYZ p2, XYZ p3, XYZ dir, double length)
        {
            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(p0, p1));
            loop.Append(Line.CreateBound(p1, p2));
            loop.Append(Line.CreateBound(p2, p3));
            loop.Append(Line.CreateBound(p3, p0));
            return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, dir, length);
        }

        /// <summary>
        /// Sweeps the family bounding box along the displacement (conservative prism).
        /// Ignores elements already touching it before moving, its host, the chain, frontiers
        /// (already analyzed), and pipes/conduits/fittings (do not block).
        /// </summary>
        private static void CheckCollisions(CtpSession s, CtpPlan r, XYZ d)
        {
            Document doc = s.Doc;
            FamilyInstance fam = r.Family;

            BoundingBoxXYZ bb = fam.get_BoundingBox(null);
            double dl = d.GetLength();
            if (bb == null || dl < 1e-9) return;

            XYZ dir = d.Normalize();
            XYZ helper = Math.Abs(dir.Z) < 0.9 ? XYZ.BasisZ : XYZ.BasisX;
            XYZ e1 = dir.CrossProduct(helper).Normalize();
            XYZ e2 = dir.CrossProduct(e1).Normalize();

            var corners = new List<XYZ>();
            foreach (double cx in new[] { bb.Min.X, bb.Max.X })
                foreach (double cy in new[] { bb.Min.Y, bb.Max.Y })
                    foreach (double cz in new[] { bb.Min.Z, bb.Max.Z })
                        corners.Add(bb.Transform.OfPoint(new XYZ(cx, cy, cz)));

            double sMin = corners.Min(k => k.DotProduct(dir)), sMax = corners.Max(k => k.DotProduct(dir));
            double aMin = corners.Min(k => k.DotProduct(e1)), aMax = corners.Max(k => k.DotProduct(e1));
            double bMin = corners.Min(k => k.DotProduct(e2)), bMax = corners.Max(k => k.DotProduct(e2));

            double side = Cfg.MmToFt;                                  // 1 mm lateral margin
            if (aMax - aMin > 4 * side) { aMin += side; aMax -= side; }
            if (bMax - bMin > 4 * side) { bMin += side; bMax -= side; }

            double shrink = Cfg.SweepEndShrinkMm * Cfg.MmToFt;         // 5 mm shrink at ends
            double startS = sMin + shrink;
            double lenStart = (sMax - sMin) - 2 * shrink;
            if (lenStart <= 0) { startS = sMin; lenStart = sMax - sMin; }
            if (lenStart <= 1e-6) return;
            double lenSweep = lenStart + dl;

            XYZ q = dir.Multiply(startS);
            XYZ p0 = q.Add(e1.Multiply(aMin)).Add(e2.Multiply(bMin));
            XYZ p1 = q.Add(e1.Multiply(aMax)).Add(e2.Multiply(bMin));
            XYZ p2 = q.Add(e1.Multiply(aMax)).Add(e2.Multiply(bMax));
            XYZ p3 = q.Add(e1.Multiply(aMin)).Add(e2.Multiply(bMax));

            Solid sweep, startSolid;
            try
            {
                sweep = Extrude(p0, p1, p2, p3, dir, lenSweep);
                startSolid = Extrude(p0, p1, p2, p3, dir, lenStart);
            }
            catch
            {
                return;
            }

            var all = new List<XYZ>(corners);
            foreach (XYZ k in corners) all.Add(k.Add(d));
            double pad = Cfg.MmToFt;
            XYZ oMin = new XYZ(all.Min(k => k.X) - pad, all.Min(k => k.Y) - pad, all.Min(k => k.Z) - pad);
            XYZ oMax = new XYZ(all.Max(k => k.X) + pad, all.Max(k => k.Y) + pad, all.Max(k => k.Z) + pad);

            var exclude = new HashSet<ElementId>(r.ChainIds);
            foreach (ElementId id in r.FrontierIds) exclude.Add(id);
            exclude.Add(fam.Id);
            exclude.Add(s.PipeId);
            try { if (fam.Host != null) exclude.Add(fam.Host.Id); } catch { }

            ICollection<ElementId> hits, hitsNow;
            try
            {
                hits = HitsOf(doc, sweep, oMin, oMax);
                hitsNow = HitsOf(doc, startSolid, oMin, oMax);   // already touching before moving
            }
            catch
            {
                return;
            }

            XYZ center = CenterOf(fam);
            Element bestEl = null;
            XYZ bestPt = null;
            double bestDist = double.MaxValue;
            foreach (ElementId id in hits)
            {
                if (exclude.Contains(id) || hitsNow.Contains(id)) continue;
                Element el = doc.GetElement(id);
                if (el == null) continue;
                if (el is MEPCurve || IsFitting(el)) continue;   // pipes/conduits/fittings do not block

                XYZ pt = IntersectionCenter(el, oMin, oMax);
                double dist = pt.DistanceTo(center);
                if (dist < bestDist) { bestDist = dist; bestPt = pt; bestEl = el; }
            }

            if (bestEl == null) return;

            r.Fail(ConflictKind.Collision, bestPt, bestEl.Id,
                "The element displacement intersects another element in the model.");
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

        // ------------------------- Apply (single Transaction) -------------------------

        private static ConflictInfo ApplyAlignment(CtpSession s, CtpPlan r)
        {
            Document doc = s.Doc;
            FamilyInstance fam = r.Family;
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

                    // 1) User decisions on frontier families
                    bool movedFrontier = false;
                    foreach (FrontierInfo fi in r.Frontiers)
                    {
                        FrontierPolicy pol;
                        if (!s.Policies.TryGetValue(fi.Family.Id, out pol)) continue;

                        if (pol == FrontierPolicy.Disconnect)
                        {
                            fi.ChainSide.DisconnectFrom(fi.FamilySide);
                        }
                        else
                        {
                            // Detach branches that would compromise a fixed third element
                            foreach (DominoRisk risk in fi.Risks)
                                risk.OnFamily.DisconnectFrom(risk.FirstOther);

                            ElementTransformUtils.MoveElement(doc, fi.Family.Id, r.Displacement);
                            movedFrontier = true;
                        }
                    }

                    // 2) Move family (if a frontier already moved, recompute remaining delta)
                    XYZ d = r.Displacement;
                    if (movedFrontier)
                    {
                        doc.Regenerate();
                        string err;
                        d = ComputeDisplacement(s, out err);
                        if (err != null) d = XYZ.Zero;
                    }

                    if (d.GetLength() >= Cfg.AlignTolFt)
                        MoveChain(doc, r, d);

                    TransactionStatus st = t.Commit();
                    if (st == TransactionStatus.Committed) return null;

                    return r.Fail(ConflictKind.RevitRejected, CenterOf(fam), fam.Id,
                        "Revit rejected the adjustment" +
                        (string.IsNullOrEmpty(pre.ErrorText) ? "." : ": " + pre.ErrorText));
                }
                catch (Exception ex)
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    return r.Fail(ConflictKind.RevitRejected, CenterOf(fam), fam.Id,
                        "Revit rejected the adjustment: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Moves the family together with its fittings as a single body and adjusts the length of
        /// neighboring straight segments: near end follows the move and far end stays fixed.
        /// </summary>
        private static void MoveChain(Document doc, CtpPlan r, XYZ d)
        {
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

            // 1) Disconnect joints with neighbors that will stretch
            foreach (ChainJoint j in r.Joints)
            {
                if (!stretched.Contains(j.NeighbourId)) continue;
                Connector a = FindConnector(doc, j.MoverId, j.MoverConnId);
                Connector b = FindConnector(doc, j.NeighbourId, j.NeighbourConnId);
                if (a != null && b != null) a.DisconnectFrom(b);
            }

            // 2) Family + fittings move together
            ElementTransformUtils.MoveElements(doc, moveIds, d);

            // 3) Neighbors lengthen / shorten
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

        // ------------------------- Highlight + Section Box -------------------------

        internal static void FocusConflict(AlignSession vis, UIDocument uidoc)
        {
            if (vis == null || vis.Conflict == null) return;
            Document doc = vis.Document;
            View view = doc.ActiveView;

            if (view is View3D v3)
            {
                using (var t = new Transaction(doc, "Focus clash"))
                {
                    t.Start();
                    try
                    {
                        if (!vis.SectionSaved)
                        {
                            vis.PrevSectionActive = v3.IsSectionBoxActive;
                            vis.PrevSectionBox = v3.GetSectionBox();
                            vis.SectionSaved = true;
                        }

                        BoundingBoxXYZ focus = ComputeFocusBox(vis, doc);
                        v3.SetSectionBox(new BoundingBoxXYZ
                        {
                            Min = focus.Min,
                            Max = focus.Max,
                            Transform = Transform.Identity
                        });
                        v3.IsSectionBoxActive = true;
                        t.Commit();
                    }
                    catch
                    {
                        if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    }
                }
            }

            ZoomToConflict(vis, uidoc);
        }

        private static void HighlightConflict(CtpSession s, UIDocument uidoc)
        {
            Document doc = s.Doc;
            AlignSession vis = s.Vis;
            ConflictInfo c = vis.Conflict;
            View view = doc.ActiveView;
            vis.ViewId = view.Id;

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

                    var toSave = new List<ElementId>(orangeIds);
                    if (!c.ConflictElementId.Equals(ElementId.InvalidElementId)) toSave.Add(c.ConflictElementId);

                    foreach (ElementId id in toSave)
                    {
                        if (doc.GetElement(id) == null) continue;
                        if (!vis.SavedOverrides.ContainsKey(id))
                            vis.SavedOverrides[id] = view.GetElementOverrides(id);
                    }

                    foreach (ElementId id in orangeIds)
                        if (doc.GetElement(id) != null) view.SetElementOverrides(id, orange);

                    if (!c.ConflictElementId.Equals(ElementId.InvalidElementId) &&
                        doc.GetElement(c.ConflictElementId) != null)
                        view.SetElementOverrides(c.ConflictElementId, red);

                    ApplySectionBox(vis, doc, view);

                    t.Commit();
                    vis.VisualsActive = true;
                }
                catch
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    vis.SavedOverrides.Clear();
                    vis.SectionSaved = false;
                }
            }

            ZoomToConflict(vis, uidoc);
        }

        private static void ApplySectionBox(AlignSession vis, Document doc, View view)
        {
            View3D v3 = view as View3D;
            if (v3 == null) return;

            if (!vis.SectionSaved)
            {
                vis.PrevSectionActive = v3.IsSectionBoxActive;
                vis.PrevSectionBox = v3.GetSectionBox();
                vis.SectionSaved = true;
            }

            BoundingBoxXYZ focus = ComputeFocusBox(vis, doc);
            var box = new BoundingBoxXYZ
            {
                Min = focus.Min,
                Max = focus.Max,
                Transform = Transform.Identity
            };
            v3.SetSectionBox(box);
            v3.IsSectionBoxActive = true;
        }

        private static void ZoomToConflict(AlignSession vis, UIDocument uidoc)
        {
            if (uidoc == null || vis.Conflict == null) return;
            Document doc = vis.Document;
            BoundingBoxXYZ box = ComputeFocusBox(vis, doc);

            try
            {
                foreach (UIView uv in uidoc.GetOpenUIViews())
                {
                    if (uv.ViewId.Equals(vis.ViewId))
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
                    if (!vis.Conflict.ConflictElementId.Equals(ElementId.InvalidElementId))
                        ids.Add(vis.Conflict.ConflictElementId);
                    else
                        ids.Add(vis.Conflict.CurveId);
                    uidoc.ShowElements(ids);
                }
                catch { }
            }
        }

        private static BoundingBoxXYZ ComputeFocusBox(AlignSession vis, Document doc)
        {
            ConflictInfo c = vis.Conflict;
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

            Element fe = doc.GetElement(c.CurveId);
            if (fe != null)
            {
                BoundingBoxXYZ fb = fe.get_BoundingBox(null);
                if (fb != null) { inc(fb.Min); inc(fb.Max); }
            }

            double ext = Math.Max(x1 - x0, Math.Max(y1 - y0, z1 - z0));
            double m = Math.Max(Cfg.FocusMarginMinMm * Cfg.MmToFt,
                Math.Min(Cfg.FocusMarginMaxMm * Cfg.MmToFt, ext * Cfg.FocusMarginFactor));

            return new BoundingBoxXYZ
            {
                Min = new XYZ(x0 - m, y0 - m, z0 - m),
                Max = new XYZ(x1 + m, y1 + m, z1 + m)
            };
        }

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
    }

    // ------------------------------------------------------------------------------
    //  API Bridge (ExternalEvent) for the conflict window
    // ------------------------------------------------------------------------------
    internal sealed class CtpHandler : IExternalEventHandler
    {
        public AlignRequest Request;
        public CtpSession Session;
        public AlignWindow Window;

        public void Execute(UIApplication app)
        {
            AlignRequest req = Request;
            UIDocument uidoc = null;
            try
            {
                uidoc = app.ActiveUIDocument;
                if (uidoc != null && !uidoc.Document.Equals(Session.Doc)) uidoc = null;
            }
            catch { uidoc = null; }

            bool closing = false;
            try
            {
                switch (req)
                {
                    case AlignRequest.Cleanup:
                        closing = true;
                        AlignEngine.ClearVisuals(Session.Vis, uidoc);
                        break;

                    case AlignRequest.Retry:
                        closing = RunEvaluation(uidoc);
                        break;

                    case AlignRequest.DisconnectAndAlign:
                        if (Session.Vis.Conflict != null && Session.Vis.Conflict.Kind == ConflictKind.Frontier)
                            Session.Policies[Session.Vis.Conflict.ConflictElementId] = FrontierPolicy.Disconnect;
                        closing = RunEvaluation(uidoc);
                        break;

                    case AlignRequest.MoveWithFamily:
                        if (Session.Vis.Conflict != null && Session.Vis.Conflict.Kind == ConflictKind.Frontier)
                        {
                            if (Session.Vis.Conflict.RiskCount > 0)
                            {
                                var td = new TaskDialog("Move with Element");
                                td.MainInstruction = "Domino effect detected";
                                td.MainContent =
                                    "Moving the other family would compromise " + Session.Vis.Conflict.RiskCount +
                                    " branch(es) connected to other fixed elements:\n" +
                                    Session.Vis.Conflict.RiskText +
                                    "\n\nThose branches will be DISCONNECTED before moving. Continue?";
                                td.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                                if (td.Show() != TaskDialogResult.Yes) break;
                            }
                            Session.Policies[Session.Vis.Conflict.ConflictElementId] = FrontierPolicy.MoveWith;
                        }
                        closing = RunEvaluation(uidoc);
                        break;
                }
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

        /// <summary>Clears highlighting, re-evaluates, and returns true if aligned (closes window).</summary>
        private bool RunEvaluation(UIDocument uidoc)
        {
            AlignEngine.ClearVisuals(Session.Vis, uidoc);
            bool done = CtpEngine.Run(Session, uidoc);
            if (done)
            {
                CtpEngine.Finish(Session);
                return true;
            }
            Window.ShowConflict(Session.Vis);
            return false;
        }

        public string GetName() { return "ConnectorToPipe.ExternalHandler"; }
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
        internal static AlignWindow ActiveWindow;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            if (ActiveWindow != null)
            {
                try { ActiveWindow.Activate(); return Result.Cancelled; }
                catch { ActiveWindow = null; }
            }

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

            // ---- 4) Session + engine ----
            var vis = new AlignSession { Document = doc };
            vis.TargetIds.Add(fam.Id);

            var session = new CtpSession
            {
                Doc = doc,
                PipeId = pipe.Id,
                FamilyId = fam.Id,
                ConnectorId = conn.Id,
                Vis = vis
            };

            bool done;
            try
            {
                done = CtpEngine.Run(session, uidoc);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                AlignEngine.ClearVisuals(vis, uidoc);
                return Result.Failed;
            }

            if (done)
            {
                CtpEngine.Finish(session);
                return Result.Succeeded;
            }

            // ---- 5) Conflict window (modeless) ----
            var handler = new CtpHandler { Session = session };
            ExternalEvent extEvent = ExternalEvent.Create(handler);

            var win = new AlignWindow(vis);
            handler.Window = win;
            win.RequestAction = req =>
            {
                handler.Request = req;
                win.SetBusy(true);
                extEvent.Raise();
            };
            win.Closed += (s, e) => ActiveWindow = null;

            try { new WindowInteropHelper(win).Owner = Process.GetCurrentProcess().MainWindowHandle; }
            catch { }

            ActiveWindow = win;
            win.ShowConflict(vis);
            win.Show();

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
