// =====================================================================================
//  DuctAlignEngine.cs   (namespace DuctAligner)
//
//  Engine + conflict window + external-event bridge shared by:
//    (a) Align Duct to Duct          (DuctAlignDuctsCommand)       -> pivot = a duct
//    (b) Align Duct to Connector     (DuctAlignToConnectorCommand) -> pivot = a family connector
//
//  Port of the AlignPipes / AlignToConnector engine adapted to rigid ducts.
//  ALIGNMENT STRATEGY (identical to AlignPipes):
//   - Only the target duct + its fittings (+ air terminals) are candidates to move.
//   - Adjacent rigid ducts are NEVER dragged along: their far end stays fixed and the near end follows
//     through Revit's own connection logic. A change of angle is NOT an error; only a collapsed or
//     inverted adjacent run is reported.
//   - Three movement strategies are tried in order (each failed attempt is rolled back completely):
//       0 = move only the duct (fittings adjust by themselves)
//       1 = move the duct and all its fittings / terminals together
//       2 = reassign the duct's location curve
//   - Every target is aligned independently to the SAME pivot, so after the command all targets
//     lie on the pivot's line (the axis is chosen once from the mean pivot -> targets vector).
//  Adapted to rigid ducts:
//   - Only rigid Duct (never FlexDuct) is selected / propagated. FlexDuct is a boundary.
//   - DuctFitting        -> moves with the chain (elbows, tees, unions, transitions).
//   - AirTerminal        -> moves with the chain (connected or hosted on the moving duct).
//   - DuctAccessory      -> BOUNDARY (needs an explicit user choice).
//   - Profile SHAPE compatibility is validated (Round / Rectangular / Oval); sizes may differ.
//   - Collision sweep uses the real profile (diameter, or width x height with orientation).
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
    // ------------------------------------------------------------------------------
    //  Data models
    // ------------------------------------------------------------------------------
    internal enum DuctAxis { EqualizeY, EqualizeX, Auto }

    internal enum DuctFrontierPolicy { Disconnect, MoveWith }

    internal enum DuctConflictKind
    {
        Frontier,          // chain touches a boundary (accessory, flex duct, equipment, terminal-less device...)
        NoSlack,
        Skew,              // legacy: no longer reported (kept so other files still compile)
        Collision,
        RevitRejected,
        InvalidGeometry,
        ProfileMismatch    // Round vs Rectangular vs Oval
    }

    internal enum DuctAlignRequest { Retry, DisconnectAndAlign, MoveWithFamily, Cleanup }

    internal sealed class DuctConflictInfo
    {
        public DuctConflictKind Kind;
        public XYZ Point;
        public ElementId CurveId;
        public ElementId ConflictElementId = ElementId.InvalidElementId;
        public HashSet<ElementId> ChainIds = new HashSet<ElementId>();
        public string Message;
        public int RiskCount;
        public string RiskText = "";
    }

    internal sealed class DuctDominoRisk
    {
        public Connector OnFamily;
        public Connector FirstOther;
        public Element Blocker;
        public XYZ Point;
        public string Reason;
    }

    internal sealed class DuctFrontierInfo
    {
        public Element Family;
        public Connector ChainSide;
        public Connector FamilySide;
        public List<Connector[]> Pairs = new List<Connector[]>();
        public List<DuctDominoRisk> Risks = new List<DuctDominoRisk>();
    }

    /// <summary>Legacy (unused): neighbouring ducts are no longer stretched manually. Kept so other files still compile.</summary>
    internal sealed class DuctChainJoint
    {
        public ElementId MoverId;
        public int MoverConnId;
        public ElementId NeighbourId;
        public int NeighbourConnId;
        public XYZ Origin;
    }

    internal sealed class DuctChainResult
    {
        public MEPCurve Curve;
        public XYZ Displacement = XYZ.Zero;
        public bool AlreadyAligned;
        /// <summary>The duct plus every connected fitting / air terminal (they all travel together in mode 1).</summary>
        public HashSet<ElementId> MoverIds = new HashSet<ElementId>();
        /// <summary>Air terminals hosted on the target duct itself (they follow it in mode 0).</summary>
        public HashSet<ElementId> HostedIds = new HashSet<ElementId>();
        /// <summary>Legacy (unused).</summary>
        public List<DuctChainJoint> Joints = new List<DuctChainJoint>();
        public HashSet<ElementId> ChainIds = new HashSet<ElementId>();
        public HashSet<ElementId> FrontierIds = new HashSet<ElementId>();
        public List<DuctFrontierInfo> Frontiers = new List<DuctFrontierInfo>();
        public List<KeyValuePair<Connector, Connector>> PivotLinks = new List<KeyValuePair<Connector, Connector>>();
        public DuctConflictInfo Conflict;

        public DuctConflictInfo Fail(DuctConflictKind kind, XYZ point, ElementId element, string msg)
        {
            Conflict = new DuctConflictInfo
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

    internal sealed class DuctSession
    {
        public Document Document;
        public ElementId PivotId;
        public int PivotConnectorId = -1;          // >= 0: pivot is a family connector; -1: pivot is a duct
        public XYZ PivotPointOverride;
        public DuctAxis Axis;
        public List<ElementId> TargetIds = new List<ElementId>();
        public int NextIndex;
        public bool ForceRigid;                    // legacy (unused): neighbouring ducts are never dragged any more
        public string Note;

        public Dictionary<ElementId, DuctFrontierPolicy> Policies = new Dictionary<ElementId, DuctFrontierPolicy>();
        public bool DisconnectAll;
        public DuctConflictInfo Conflict;

        public ElementId ViewId = ElementId.InvalidElementId;
        public Dictionary<ElementId, OverrideGraphicSettings> SavedOverrides =
            new Dictionary<ElementId, OverrideGraphicSettings>();
        public bool SectionSaved;
        public bool PrevSectionActive;
        public BoundingBoxXYZ PrevSectionBox;
        public bool VisualsActive;
    }

    // ------------------------------------------------------------------------------
    //  Engine
    // ------------------------------------------------------------------------------
    internal static class DuctEngine
    {
        // ------------------------- Main flow -------------------------

        public static bool ProcessPending(DuctSession s, UIDocument uidoc)
        {
            Document doc = s.Document;
            s.Conflict = null;

            while (s.NextIndex < s.TargetIds.Count)
            {
                MEPCurve p = doc.GetElement(s.TargetIds[s.NextIndex]) as MEPCurve;
                if (p == null || !DuctUtil.IsRigidDuct(p)) { s.NextIndex++; continue; }

                DuctChainResult r = Evaluate(doc, s, p);

                if (r.AlreadyAligned)
                {
                    s.Note = "Duct [Id " + p.Id + "] was not moved: it is already aligned (difference " +
                             (r.Displacement.GetLength() / DuctCfg.MmToFt).ToString("0.##") + " mm).";
                    s.NextIndex++;
                    continue;
                }

                if (r.Conflict == null)
                {
                    // Tries the movement strategies in order; each failed attempt is rolled back completely.
                    DuctConflictInfo firstFailure = null;
                    bool applied = false;
                    for (int mode = 0; mode < 3 && !applied; mode++)
                    {
                        DuctConflictInfo c = ApplyAlignment(doc, s, r, mode);
                        if (c == null) { applied = true; break; }
                        if (firstFailure == null) firstFailure = c;
                        if (c.Kind != DuctConflictKind.RevitRejected) { firstFailure = c; break; }
                    }
                    r.Conflict = applied ? null : firstFailure;
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

        // ------------------------- Displacement -------------------------

        public static XYZ PivotCenter(Document doc, DuctSession s)
        {
            if (s.PivotConnectorId >= 0)
            {
                Connector pc = DuctUtil.FindConnector(doc, s.PivotId, s.PivotConnectorId);
                if (pc != null) return pc.Origin;
                if (s.PivotPointOverride != null) return s.PivotPointOverride;
            }
            return DuctUtil.MidPoint(doc.GetElement(s.PivotId));
        }

        private static void AddUnique(List<XYZ> list, XYZ v)
        {
            foreach (XYZ k in list)
                if (k.DistanceTo(v) < 1e-6) return;
            list.Add(v);
        }

        /// <summary>Vertical movement so the axis of a HORIZONTAL duct passes through the connector (connector pivot only).</summary>
        private static double ZDelta(DuctSession s, Line axis, XYZ c, XYZ mid, XYZ planDisp)
        {
            if (s.PivotConnectorId < 0) return 0;
            XYZ dir = axis.Direction;
            if (Math.Abs(dir.Z) > DuctCfg.HorizontalMaxSlope) return 0;

            double hh = dir.X * dir.X + dir.Y * dir.Y;
            if (hh < 1e-9) return 0;

            double t = ((c.X - mid.X - planDisp.X) * dir.X + (c.Y - mid.Y - planDisp.Y) * dir.Y) / hh;
            double zOnAxis = mid.Z + planDisp.Z + dir.Z * t;
            return c.Z - zOnAxis;
        }

        private static XYZ WithZ(DuctSession s, Line axis, XYZ c, XYZ mid, XYZ planDisp)
        {
            return new XYZ(planDisp.X, planDisp.Y, ZDelta(s, axis, c, mid, planDisp));
        }

        private static List<XYZ> ComputeCandidates(Document doc, DuctSession s, Line axis, out string error)
        {
            var list = new List<XYZ>();
            error = null;

            XYZ c = PivotCenter(doc, s);
            if (c == null) { error = "The reference could not be read."; return list; }

            XYZ mid = axis.Evaluate(0.5, true);
            if (s.Axis != DuctAxis.Auto)
            {
                bool useY = s.Axis == DuctAxis.EqualizeY;
                XYZ dir = useY ? XYZ.BasisY : XYZ.BasisX;
                if (Math.Abs(axis.Direction.DotProduct(dir)) > 0.999)
                {
                    useY = !useY;
                    dir = useY ? XYZ.BasisY : XYZ.BasisX;
                    if (Math.Abs(axis.Direction.DotProduct(dir)) > 0.999)
                    {
                        error = "The duct is vertical and there is no plan direction that aligns it.";
                        return list;
                    }
                }
                double deltaFixed = useY ? c.Y - mid.Y : c.X - mid.X;
                list.Add(WithZ(s, axis, c, mid, dir.Multiply(deltaFixed)));
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
            AddUnique(list, WithZ(s, axis, c, mid, n.Multiply(delta)));
            if (Math.Abs(n.X) > 0.2) AddUnique(list, WithZ(s, axis, c, mid, new XYZ(delta / n.X, 0, 0)));
            if (Math.Abs(n.Y) > 0.2) AddUnique(list, WithZ(s, axis, c, mid, new XYZ(0, delta / n.Y, 0)));
            return list;
        }

        private static XYZ ComputeDisplacement(Document doc, DuctSession s, Line axis, out string error)
        {
            List<XYZ> list = ComputeCandidates(doc, s, axis, out error);
            return list.Count > 0 ? list[0] : XYZ.Zero;
        }

        // ------------------------- Chain evaluation -------------------------

        private enum NbResult { Ok, NoGeometry, Collapse }

        private static DuctChainResult Evaluate(Document doc, DuctSession s, MEPCurve p)
        {
            var r = new DuctChainResult { Curve = p };
            r.ChainIds.Add(p.Id);

            Line axis = DuctUtil.GetLine(p);
            if (axis == null)
            {
                r.Fail(DuctConflictKind.InvalidGeometry, DuctUtil.CenterOf(p), p.Id,
                    "The duct does not have an evaluable linear geometry.");
                return r;
            }

            // ----- Profile shape: duct vs reference connector -----
            if (s.PivotConnectorId >= 0)
            {
                Connector pc = DuctUtil.FindConnector(doc, s.PivotId, s.PivotConnectorId);
                if (pc != null)
                {
                    DuctProfile dp = DuctProfiles.FromDuct(p);
                    DuctProfile cp = DuctProfiles.FromConnector(pc);
                    if (!DuctProfiles.ShapeCompatible(dp, cp))
                    {
                        r.Fail(DuctConflictKind.ProfileMismatch, pc.Origin, s.PivotId,
                            DuctProfiles.MismatchMessage(dp, cp));
                        return r;
                    }
                }
            }

            string err;
            List<XYZ> candidates = ComputeCandidates(doc, s, axis, out err);
            if (err != null || candidates.Count == 0)
            {
                r.Fail(DuctConflictKind.InvalidGeometry, DuctUtil.CenterOf(p), p.Id, err ?? "The displacement could not be calculated.");
                return r;
            }
            XYZ d = candidates[0];
            r.Displacement = d;
            if (d.GetLength() < DuctCfg.AlignTolFt) { r.AlreadyAligned = true; return r; }

            // ===== Pass 1: main chain through HVAC connectors =====
            var movers = new HashSet<ElementId> { p.Id };
            var queue = new Queue<Element>();
            queue.Enqueue(p);

            var neighbours = new Dictionary<ElementId, List<XYZ>>();
            DuctConflictInfo profileFail = null;

            while (queue.Count > 0)
            {
                Element cur = queue.Dequeue();

                // Air terminals hosted on a moving duct travel with it
                if (cur is Duct)
                {
                    foreach (Element h in DuctUtil.HostedTerminals(cur))
                    {
                        if (movers.Contains(h.Id) || h.Id.Equals(s.PivotId)) continue;
                        if (cur.Id.Equals(p.Id)) r.HostedIds.Add(h.Id);
                        movers.Add(h.Id);
                        r.ChainIds.Add(h.Id);
                        queue.Enqueue(h);
                    }
                }

                foreach (Connector c in DuctUtil.GetConnectors(cur))
                {
                    foreach (Connector other in DuctUtil.GetRefs(c, cur))
                    {
                        Element o = other.Owner;
                        if (o == null || movers.Contains(o.Id)) continue;

                        // The pivot never moves and is never a boundary: only record the link to detach it on apply
                        if (o.Id.Equals(s.PivotId))
                        {
                            r.PivotLinks.Add(new KeyValuePair<Connector, Connector>(c, other));
                            continue;
                        }

                        if (DuctUtil.IsMover(o))
                        {
                            movers.Add(o.Id);
                            r.ChainIds.Add(o.Id);
                            queue.Enqueue(o);
                        }
                        else if (DuctUtil.IsRigidDuct(o))
                        {
                            // Same SHAPE required; different sizes are allowed
                            if (!DuctProfiles.Compatible(c, other))
                            {
                                if (profileFail == null)
                                    profileFail = new DuctConflictInfo
                                    {
                                        Kind = DuctConflictKind.ProfileMismatch,
                                        Point = other.Origin,
                                        ConflictElementId = o.Id,
                                        Message = DuctProfiles.MismatchMessage(
                                            DuctProfiles.FromConnector(c), DuctProfiles.FromConnector(other))
                                    };
                                continue;
                            }

                            // Adjacent rigid duct: it is NEVER dragged along (same as AlignPipes). Its far end stays
                            // fixed and its near end follows through Revit's connection logic.
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
                            // Boundary: DuctAccessory, FlexDuct, equipment, other families
                            DuctFrontierInfo known = null;
                            foreach (DuctFrontierInfo f in r.Frontiers)
                                if (f.Family.Id.Equals(o.Id)) { known = f; break; }
                            if (known == null)
                            {
                                known = new DuctFrontierInfo { Family = o, ChainSide = c, FamilySide = other };
                                r.Frontiers.Add(known);
                                r.FrontierIds.Add(o.Id);
                            }
                            known.Pairs.Add(new[] { c, other });
                        }
                    }
                }
            }
            foreach (ElementId mid in movers) r.MoverIds.Add(mid);

            if (profileFail != null)
            {
                r.Fail(profileFail.Kind, profileFail.Point, profileFail.ConflictElementId, profileFail.Message);
                return r;
            }

            // ===== Pass 2: boundaries + domino effect =====
            foreach (DuctFrontierInfo fi in r.Frontiers)
            {
                AnalyzeDomino(doc, p, d, fi, r.ChainIds);

                if (s.DisconnectAll || s.Policies.ContainsKey(fi.Family.Id)) continue;

                string what = DuctUtil.IsDuctAccessory(fi.Family) ? "a duct accessory"
                            : DuctUtil.IsFlexDuct(fi.Family) ? "a flexible duct"
                            : "a boundary family";
                DuctConflictInfo cf = r.Fail(DuctConflictKind.Frontier, fi.FamilySide.Origin, fi.Family.Id,
                    "The chain touches " + what + ". Choose 'Disconnect and Align' (aligns only the duct) " +
                    "or 'Move with Element' (the element moves together with the chain).");
                cf.RiskCount = fi.Risks.Count;
                cf.RiskText = string.Join("\n", fi.Risks.Select(k =>
                    "• " + DuctUtil.Describe(doc, k.Blocker == null ? ElementId.InvalidElementId : k.Blocker.Id) +
                    " — " + k.Reason));
                return r;
            }

            // ===== Adjacent rigid runs: only a REAL collapse blocks (no skew errors) =====
            foreach (var kv in neighbours)
            {
                MEPCurve q = doc.GetElement(kv.Key) as MEPCurve;
                if (q == null) continue;
                r.ChainIds.Add(q.Id);

                if (kv.Value.Count > 1) continue;

                XYZ near;
                double amount;
                NbResult nb = CheckNeighbour(q, kv.Value[0], d, out near, out amount);
                if (nb != NbResult.Collapse) continue;

                r.Fail(DuctConflictKind.NoSlack, near, q.Id,
                    "An adjacent duct would collapse or invert (it is missing " +
                    (amount / DuctCfg.MmToFt).ToString("0.#") + " mm of length to follow the movement).");
                return r;
            }

            // ===== Pass 3: physical collision =====
            DuctConflictInfo col = CheckCollisions(doc, s, p, axis, d, r);
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

            Line ql = DuctUtil.GetLine(q);
            if (ql == null) return NbResult.NoGeometry;

            XYZ e0 = ql.GetEndPoint(0), e1 = ql.GetEndPoint(1);
            near = e0.DistanceTo(junction) <= e1.DistanceTo(junction) ? e0 : e1;
            XYZ far = near.IsAlmostEqualTo(e0) ? e1 : e0;

            XYZ v = near.Subtract(far);
            if (v.GetLength() < 1e-9) return NbResult.NoGeometry;
            XYZ v2 = near.Add(d).Subtract(far);

            double minLen = DuctCfg.MinNeighbourLengthMm * DuctCfg.MmToFt;
            double len2 = v2.GetLength();
            bool flipped = v2.DotProduct(v) <= 0;
            if (flipped || len2 < minLen)
            {
                amountFt = flipped ? len2 + minLen : minLen - len2;
                return NbResult.Collapse;
            }
            return NbResult.Ok;
        }

        // ------------------------- Domino effect -------------------------

        private static void AnalyzeDomino(Document doc, MEPCurve p, XYZ d, DuctFrontierInfo fi, HashSet<ElementId> chainIds)
        {
            var visited = new HashSet<ElementId>(chainIds) { fi.Family.Id };

            foreach (Connector fc in DuctUtil.GetConnectors(fi.Family))
            {
                bool isJoint = false;
                foreach (Connector[] pr in fi.Pairs)
                    if (pr[1].Id == fc.Id) { isJoint = true; break; }
                if (isJoint) continue;

                foreach (Connector first in DuctUtil.GetRefs(fc, fi.Family))
                {
                    if (first.Owner == null || visited.Contains(first.Owner.Id)) continue;
                    DuctDominoRisk risk = TraceBranch(d, fc, first, visited);
                    if (risk != null) fi.Risks.Add(risk);
                }
            }
        }

        private static DuctDominoRisk TraceBranch(XYZ d, Connector fc, Connector first, HashSet<ElementId> visited)
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

                if (DuctUtil.IsMover(o))
                {
                    foreach (Connector c in DuctUtil.GetConnectors(o))
                    {
                        if (c.Id == entry.Id) continue;
                        foreach (Connector other in DuctUtil.GetRefs(c, o))
                            queue.Enqueue(new KeyValuePair<Element, Connector>(other.Owner, other));
                    }
                }
                else if (DuctUtil.IsRigidDuct(o))
                {
                    XYZ near;
                    double amount;
                    NbResult nb = CheckNeighbour((MEPCurve)o, entry.Origin, d, out near, out amount);
                    if (nb == NbResult.Collapse)
                        return new DuctDominoRisk
                        {
                            OnFamily = fc,
                            FirstOther = first,
                            Blocker = o,
                            Point = near,
                            Reason = "the duct does not have enough length to absorb the movement"
                        };
                }
                else
                {
                    return new DuctDominoRisk
                    {
                        OnFamily = fc,
                        FirstOther = first,
                        Blocker = o,
                        Point = entry.Origin,
                        Reason = "connected element that would be dragged along"
                    };
                }
            }
            return null;
        }

        // ------------------------- Collision (sweep with the real profile) -------------------------

        private static DuctConflictInfo CheckCollisions(Document doc, DuctSession s, MEPCurve p, Line axis, XYZ d, DuctChainResult r)
        {
            XYZ u = axis.Direction;

            double along = d.DotProduct(u);
            XYZ dPerp = d.Subtract(u.Multiply(along));
            double dl = dPerp.GetLength();
            if (dl < 1e-6) return null;

            XYZ dirD = dPerp.Normalize();
            XYZ w = u.CrossProduct(dirD);
            if (w.GetLength() < 1e-9) return null;
            w = w.Normalize();

            // True dimensions: half extent of the profile along the sweep directions
            DuctProfile prof = DuctProfiles.FromDuct(p);
            XYZ wDir, hDir;
            DuctProfiles.CrossSectionAxes(p, u, out wDir, out hDir);
            double rA = DuctProfiles.HalfExtent(prof, wDir, hDir, dirD);   // along the displacement
            double rB = DuctProfiles.HalfExtent(prof, wDir, hDir, w);      // across it

            double shrink = DuctCfg.SweepEndShrinkMm * DuctCfg.MmToFt;
            double length = axis.Length + Math.Abs(along) - 2 * shrink;
            if (length <= 0) return null;
            XYZ start = axis.GetEndPoint(0).Add(u.Multiply(shrink + Math.Min(0, along)));

            XYZ a = start.Subtract(dirD.Multiply(rA)).Subtract(w.Multiply(rB));
            XYZ b = start.Add(dirD.Multiply(dl + rA)).Subtract(w.Multiply(rB));
            XYZ c = start.Add(dirD.Multiply(dl + rA)).Add(w.Multiply(rB));
            XYZ e = start.Subtract(dirD.Multiply(rA)).Add(w.Multiply(rB));

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
            double pad = DuctCfg.MmToFt;
            XYZ sMin = new XYZ(all.Min(q => q.X) - pad, all.Min(q => q.Y) - pad, all.Min(q => q.Z) - pad);
            XYZ sMax = new XYZ(all.Max(q => q.X) + pad, all.Max(q => q.Y) + pad, all.Max(q => q.Z) + pad);

            var exclude = new HashSet<ElementId>(r.ChainIds);
            foreach (ElementId id in r.FrontierIds) exclude.Add(id);
            foreach (ElementId id in s.TargetIds) exclude.Add(id);
            exclude.Add(p.Id);
            exclude.Add(s.PivotId);

            ICollection<ElementId> hits;
            try
            {
                hits = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory>(DuctCfg.CollisionCategories)))
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
                if (el is MEPCurve || DuctUtil.IsMover(el)) continue;   // ducts / fittings / terminals never block
                XYZ pt = IntersectionCenter(el, sMin, sMax);
                double dist = pt.DistanceTo(start);
                if (dist < bestDist) { bestDist = dist; bestPt = pt; bestEl = el; }
            }

            if (bestEl == null) return null;

            return r.Fail(DuctConflictKind.Collision, bestPt, bestEl.Id,
                "The duct's displacement (" + DuctProfiles.Describe(prof) + ") intersects another element of the model.");
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

        // ------------------------- Apply alignment (single Transaction) -------------------------

        /// <summary>
        /// mode 0: move only the duct (hosted terminals follow it; fittings and adjacent runs adjust by themselves).
        /// mode 1: move the duct and all its fittings / terminals together as one group.
        /// mode 2: reassign the duct's location curve.
        /// </summary>
        private static DuctConflictInfo ApplyAlignment(Document doc, DuctSession s, DuctChainResult r, int mode)
        {
            MEPCurve p = r.Curve;
            var pre = new DuctRollbackPreprocessor();

            using (var t = new Transaction(doc, "Align duct"))
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

                    // 1) User decisions on boundaries
                    bool movedFamily = false;
                    foreach (DuctFrontierInfo fi in r.Frontiers)
                    {
                        DuctFrontierPolicy pol;
                        if (!s.Policies.TryGetValue(fi.Family.Id, out pol))
                        {
                            if (!s.DisconnectAll) continue;
                            pol = DuctFrontierPolicy.Disconnect;
                        }

                        if (pol == DuctFrontierPolicy.Disconnect)
                        {
                            foreach (Connector[] pr in fi.Pairs)
                            {
                                try { pr[0].DisconnectFrom(pr[1]); }
                                catch { }
                            }
                        }
                        else
                        {
                            foreach (DuctDominoRisk risk in fi.Risks)
                                risk.OnFamily.DisconnectFrom(risk.FirstOther);

                            ElementTransformUtils.MoveElement(doc, fi.Family.Id, r.Displacement);
                            movedFamily = true;
                        }
                    }

                    // 2) Align (recalculate remaining delta if a boundary moved)
                    XYZ d = r.Displacement;
                    if (movedFamily)
                    {
                        doc.Regenerate();
                        Line now = DuctUtil.GetLine(p);
                        string err = null;
                        d = now != null ? ComputeDisplacement(doc, s, now, out err) : XYZ.Zero;
                        if (err != null) d = XYZ.Zero;
                    }

                    if (d.GetLength() >= DuctCfg.AlignTolFt)
                    {
                        if (mode == 0)
                        {
                            var ids = new List<ElementId> { p.Id };
                            foreach (ElementId hid in r.HostedIds) ids.Add(hid);
                            ElementTransformUtils.MoveElements(doc, ids, d);
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
                                  string.Join("; ", pre.FailingIds.Select(id => DuctUtil.Describe(doc, id)));
                    return r.Fail(DuctConflictKind.RevitRejected, DuctUtil.CenterOf(p), p.Id,
                        "Revit rejected the adjustment" + detail);
                }
                catch (Exception ex)
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    return r.Fail(DuctConflictKind.RevitRejected, DuctUtil.CenterOf(p), p.Id,
                        "Revit rejected the adjustment: " + ex.Message);
                }
            }
        }

        public static int DisconnectConflictElement(DuctSession s)
        {
            Document doc = s.Document;
            DuctConflictInfo c = s.Conflict;
            if (c == null || c.ConflictElementId == null || c.ConflictElementId.Equals(ElementId.InvalidElementId)) return 0;
            if (c.ConflictElementId.Equals(c.CurveId)) return 0;

            Element ce = doc.GetElement(c.ConflictElementId);
            if (ce == null) return 0;

            var pairs = new List<Connector[]>();
            foreach (Connector k in DuctUtil.GetConnectors(ce))
                foreach (Connector o in DuctUtil.GetRefs(k, ce))
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

        // ------------------------- Highlight + Section Box -------------------------

        private static void HighlightConflict(DuctSession s, UIDocument uidoc)
        {
            Document doc = s.Document;
            DuctConflictInfo c = s.Conflict;
            View view = doc.ActiveView;
            s.ViewId = view.Id;

            ElementId solidFill = FindSolidFillId(doc);
            OverrideGraphicSettings red = BuildOverride(new Color(255, 0, 0), solidFill);
            OverrideGraphicSettings orange = BuildOverride(new Color(255, 140, 0), solidFill);

            using (var t = new Transaction(doc, "Highlight duct clash"))
            {
                t.Start();
                try
                {
                    var orangeIds = new List<ElementId>();
                    foreach (ElementId id in c.ChainIds)
                        if (!id.Equals(c.ConflictElementId)) orangeIds.Add(id);

                    HalftoneExcept(s, doc, view, BuildConflictKeepSet(s, doc, c));

                    var toSave = new List<ElementId>(orangeIds);
                    if (!c.ConflictElementId.Equals(ElementId.InvalidElementId)) toSave.Add(c.ConflictElementId);

                    foreach (ElementId id in toSave)
                    {
                        if (doc.GetElement(id) == null) continue;
                        if (!s.SavedOverrides.ContainsKey(id))
                            s.SavedOverrides[id] = view.GetElementOverrides(id);
                    }

                    foreach (ElementId id in orangeIds)
                        if (doc.GetElement(id) != null) view.SetElementOverrides(id, orange);

                    if (!c.ConflictElementId.Equals(ElementId.InvalidElementId) &&
                        doc.GetElement(c.ConflictElementId) != null)
                        view.SetElementOverrides(c.ConflictElementId, red);

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

        private static HashSet<ElementId> BuildConflictKeepSet(DuctSession s, Document doc, DuctConflictInfo c)
        {
            var keep = new HashSet<ElementId>(c.ChainIds);
            keep.Add(c.CurveId);
            keep.Add(s.PivotId);
            if (!c.ConflictElementId.Equals(ElementId.InvalidElementId))
            {
                keep.Add(c.ConflictElementId);
                Element ce = doc.GetElement(c.ConflictElementId);
                if (ce != null)
                    foreach (Connector k in DuctUtil.GetConnectors(ce))
                        foreach (Connector o in DuctUtil.GetRefs(k, ce))
                            if (o.Owner != null) keep.Add(o.Owner.Id);
            }
            return keep;
        }

        private static void HalftoneExcept(DuctSession s, Document doc, View view, HashSet<ElementId> keep)
        {
            ICollection<ElementId> all = new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType()
                .ToElementIds();
            foreach (ElementId id in all)
            {
                if (keep.Contains(id)) continue;
                try
                {
                    if (!s.SavedOverrides.ContainsKey(id)) s.SavedOverrides[id] = view.GetElementOverrides(id);
                    DuctUtil.TrySetHalftone(view, id, true);
                }
                catch { }
            }
        }

        private static void ApplySectionBox(DuctSession s, Document doc, View view)
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
            v3.SetSectionBox(new BoundingBoxXYZ { Min = focus.Min, Max = focus.Max, Transform = Transform.Identity });
            v3.IsSectionBoxActive = true;
        }

        private static void ZoomToConflict(DuctSession s, UIDocument uidoc)
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

        private static BoundingBoxXYZ ComputeFocusBox(DuctSession s, Document doc)
        {
            DuctConflictInfo c = s.Conflict;
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
            Line axis = p != null ? DuctUtil.GetLine(p) : null;
            if (axis != null)
            {
                XYZ e0 = axis.GetEndPoint(0), e1 = axis.GetEndPoint(1);
                inc(e0.DistanceTo(c.Point) <= e1.DistanceTo(c.Point) ? e0 : e1);
            }

            double ext = Math.Max(x1 - x0, Math.Max(y1 - y0, z1 - z0));
            double m = ext * DuctCfg.FocusMarginFactor;
            m = Math.Max(m, DuctCfg.FocusMarginMinMm * DuctCfg.MmToFt);
            m = Math.Min(m, DuctCfg.FocusMarginMaxMm * DuctCfg.MmToFt);

            return new BoundingBoxXYZ
            {
                Min = new XYZ(x0 - m, y0 - m, z0 - m),
                Max = new XYZ(x1 + m, y1 + m, z1 + m)
            };
        }

        public static void ClearVisuals(DuctSession s, UIDocument uidoc)
        {
            if (!s.VisualsActive && s.SavedOverrides.Count == 0 && !s.SectionSaved) return;

            Document doc = s.Document;
            View view = doc.IsValidObject ? doc.GetElement(s.ViewId) as View : null;

            if (view != null)
            {
                using (var t = new Transaction(doc, "Restore view after duct clash"))
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

        public static string KindText(DuctConflictKind k)
        {
            switch (k)
            {
                case DuctConflictKind.Frontier: return "Boundary element in the chain";
                case DuctConflictKind.NoSlack: return "Adjacent duct is too short";
                case DuctConflictKind.Skew: return "Adjacent duct would end up skewed";
                case DuctConflictKind.Collision: return "Geometric collision";
                case DuctConflictKind.RevitRejected: return "Revit rejected the adjustment";
                case DuctConflictKind.ProfileMismatch: return "Incompatible duct profile shape";
                default: return "Geometry cannot be evaluated";
            }
        }
    }

    // ------------------------------------------------------------------------------
    //  API bridge (ExternalEvent)
    // ------------------------------------------------------------------------------
    internal sealed class DuctExternalHandler : IExternalEventHandler
    {
        public DuctAlignRequest Request;
        public DuctSession Session;
        public DuctAlignWindow Window;

        public void Execute(UIApplication app)
        {
            DuctAlignRequest req = Request;
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
                    case DuctAlignRequest.Cleanup:
                        closing = true;
                        DuctEngine.ClearVisuals(Session, uidoc);
                        break;

                    case DuctAlignRequest.Retry:
                        closing = RunEvaluation(uidoc);
                        break;

                    case DuctAlignRequest.DisconnectAndAlign:
                        if (Session.Conflict != null)
                        {
                            if (Session.Conflict.Kind == DuctConflictKind.ProfileMismatch)
                            {
                                Window.ShowConflict(Session);
                                Window.AppendNote("A profile shape incompatibility cannot be solved by disconnecting. " +
                                                  "Pick an element with the same profile shape.");
                                break;
                            }
                            if (Session.Conflict.Kind == DuctConflictKind.Frontier)
                            {
                                Session.Policies[Session.Conflict.ConflictElementId] = DuctFrontierPolicy.Disconnect;
                            }
                            else if (DuctEngine.DisconnectConflictElement(Session) == 0)
                            {
                                Session.DisconnectAll = true;
                            }
                        }
                        closing = RunEvaluation(uidoc);
                        break;

                    case DuctAlignRequest.MoveWithFamily:
                        if (Session.Conflict != null && Session.Conflict.Kind != DuctConflictKind.Frontier)
                        {
                            Window.ShowConflict(Session);
                            Window.AppendNote("'Move with Element' only applies when the chain touches a connected boundary element.");
                            break;
                        }
                        if (Session.Conflict != null && Session.Conflict.Kind == DuctConflictKind.Frontier)
                        {
                            if (Session.Conflict.RiskCount > 0)
                            {
                                var td = new TaskDialog("Move with Element");
                                td.MainInstruction = "Domino effect detected";
                                td.MainContent =
                                    "Moving the element would compromise " + Session.Conflict.RiskCount +
                                    " branch(es) connected to other fixed elements:\n" +
                                    Session.Conflict.RiskText +
                                    "\n\nThose branches will be DISCONNECTED before moving. Continue?";
                                td.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                                if (td.Show() != TaskDialogResult.Yes) break;
                            }
                            Session.Policies[Session.Conflict.ConflictElementId] = DuctFrontierPolicy.MoveWith;
                        }
                        closing = RunEvaluation(uidoc);
                        break;
                }

                if (closing && req != DuctAlignRequest.Cleanup)
                    AfterResolved(uidoc);
            }
            catch (Exception ex)
            {
                if (req == DuctAlignRequest.Cleanup) closing = true;
                else Window.SetStatus("Error", ex.Message);
            }
            finally
            {
                if (closing) Window.ForceClose();
                else Window.SetBusy(false);
            }
        }

        private bool RunEvaluation(UIDocument uidoc)
        {
            DuctEngine.ClearVisuals(Session, uidoc);
            bool done = DuctEngine.ProcessPending(Session, uidoc);
            if (done) return true;
            Window.ShowConflict(Session);
            return false;
        }

        private void AfterResolved(UIDocument uidoc)
        {
            if (uidoc != null && Session.PivotConnectorId >= 0 && Session.TargetIds.Count > 0)
            {
                try
                {
                    DuctAlignSuite.DuctPalette.ShowFor(uidoc, Session.PivotId, Session.PivotConnectorId, Session.TargetIds[0]);
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(Session.Note))
            {
                string note = Session.Note;
                Session.Note = null;
                TaskDialog.Show(DuctCfg.Caption, note);
            }
        }

        public string GetName() { return "DuctAligner.ExternalHandler"; }
    }

    // ------------------------------------------------------------------------------
    //  Conflict window (dark slate, code-only WPF)
    // ------------------------------------------------------------------------------
    internal sealed class DuctAlignWindow : Window
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

        public Action<DuctAlignRequest> RequestAction;

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

        public DuctAlignWindow()
        {
            Title = DuctCfg.Caption;
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

            var headerTitle = new TextBlock
            {
                Text = DuctCfg.Caption,
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

            _retry.Click += (s, e) => Fire(DuctAlignRequest.Retry);
            _disc.Click += (s, e) => Fire(DuctAlignRequest.DisconnectAndAlign);
            _move.Click += (s, e) => Fire(DuctAlignRequest.MoveWithFamily);
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

        private void Fire(DuctAlignRequest req)
        {
            if (_busy && req != DuctAlignRequest.Cleanup) return;
            _busy = true;
            if (RequestAction != null) RequestAction(req);
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_forceClose) return;
            e.Cancel = true;
            if (_cleanupRequested) return;
            _cleanupRequested = true;
            Fire(DuctAlignRequest.Cleanup);
        }

        public void ForceClose()
        {
            _forceClose = true;
            try { Close(); } catch { }
        }

        public void SetBusy(bool busy) { _busy = busy; }

        public void SetStatus(string title, string detail)
        {
            _title.Text = title;
            _detail.Text = detail;
        }

        public void AppendNote(string note)
        {
            _detail.Text = _detail.Text + "\n\n" + note;
        }

        public void ShowConflict(DuctSession s)
        {
            DuctConflictInfo c = s.Conflict;
            if (c == null)
            {
                SetStatus("No conflicts", "There are no pending clashes.");
                return;
            }

            Document doc = s.Document;
            double f = 304.8;
            string title = "Conflict on duct " + (s.NextIndex + 1) + " of " + s.TargetIds.Count;
            string detail =
                "Type: " + DuctEngine.KindText(c.Kind) + "\n" +
                "Duct (orange): " + DuctUtil.Describe(doc, c.CurveId) + "\n" +
                "Conflicting element (red): " + DuctUtil.Describe(doc, c.ConflictElementId) + "\n" +
                "Point (mm): X=" + (c.Point.X * f).ToString("0") +
                "  Y=" + (c.Point.Y * f).ToString("0") +
                "  Z=" + (c.Point.Z * f).ToString("0") + "\n\n" +
                c.Message;

            if (c.Kind == DuctConflictKind.Frontier)
            {
                detail += c.RiskCount > 0
                    ? "\n\nDomino effect: moving the element would compromise " + c.RiskCount +
                      " branch(es) towards fixed elements:\n" + c.RiskText
                    : "\n\nDomino effect: moving the element would only stretch free ducts.";
            }
            else if (c.Kind == DuctConflictKind.ProfileMismatch)
            {
                detail += "\n\nSelect an element with the same profile shape and run the command again.";
            }
            else
            {
                detail += "\n\nResolve the conflict in Revit and press Recalculate, or use 'Disconnect and Align' " +
                          "to detach the conflicting element.";
            }

            SetStatus(title, detail);
        }
    }

    // ------------------------------------------------------------------------------
    //  Shared launcher: runs the engine and opens the conflict window if needed
    // ------------------------------------------------------------------------------
    internal static class DuctAlignRunner
    {
        internal static DuctAlignWindow ActiveWindow;

        public static Result Run(UIDocument uidoc, DuctSession session, ref string message)
        {
            bool done;
            try
            {
                done = DuctEngine.ProcessPending(session, uidoc);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                DuctEngine.ClearVisuals(session, uidoc);
                return Result.Failed;
            }

            // Connector workflow: keep operating on the same connector / duct through the floating palette
            if (session.PivotConnectorId >= 0 && session.TargetIds.Count > 0)
            {
                try
                {
                    DuctAlignSuite.DuctPalette.ShowFor(uidoc, session.PivotId, session.PivotConnectorId, session.TargetIds[0]);
                }
                catch { }
            }

            if (done)
            {
                if (!string.IsNullOrEmpty(session.Note))
                    TaskDialog.Show(DuctCfg.Caption, session.Note);
                return Result.Succeeded;
            }

            var handler = new DuctExternalHandler { Session = session };
            ExternalEvent extEvent = ExternalEvent.Create(handler);

            var win = new DuctAlignWindow();
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
            win.ShowConflict(session);
            win.Show();

            return Result.Succeeded;
        }
    }

    // ------------------------------------------------------------------------------
    //  (a) Align Duct to Duct   (AlignPipes pattern)
    // ------------------------------------------------------------------------------
    [Transaction(TransactionMode.Manual)]
    public class DuctAlignDuctsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            if (DuctAlignRunner.ActiveWindow != null)
            {
                try { DuctAlignRunner.ActiveWindow.Activate(); return Result.Cancelled; }
                catch { DuctAlignRunner.ActiveWindow = null; }
            }

            var session = new DuctSession { Document = doc, PivotConnectorId = -1 };

            bool userCancelled = false;
            using (var selTg = new TransactionGroup(doc, "DuctAligner temporary selection"))
            {
                selTg.Start();
                try
                {
                    // ---- Pivot: 1 click (rigid duct) ----
                    Reference pr = uidoc.Selection.PickObject(ObjectType.Element,
                        new DuctSelectionFilter(ElementId.InvalidElementId),
                        "Select the PIVOT (rigid duct)");
                    session.PivotId = pr.ElementId;

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
                            if (!id.Equals(session.PivotId)) DuctUtil.TrySetHalftone(activeView, id, true);
                        DuctUtil.TrySetTint(activeView, session.PivotId, pivotColor);
                        t.Commit();
                    }

                    // ---- Targets (1..N): click again to remove; Enter / Space confirms; Esc cancels ----
                    var filter = new DuctSelectionFilter(session.PivotId);
                    using (var keyHook = new DuctKeyConfirmHook())
                    {
                        while (true)
                        {
                            try
                            {
                                Reference r = uidoc.Selection.PickObject(ObjectType.Element, filter,
                                    "Click to add/remove ducts (" + session.TargetIds.Count +
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

                                using (var t = new Transaction(doc, "Mark duct"))
                                {
                                    t.Start();
                                    if (added) DuctUtil.TrySetTint(activeView, r.ElementId, targetColor);
                                    else DuctUtil.TrySetHalftone(activeView, r.ElementId, true);
                                    t.Commit();
                                }
                            }
                            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                            {
                                if (keyHook.Confirmed) break;
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
                    if (selTg.GetStatus() == TransactionStatus.Started)
                        selTg.RollBack();
                }
            }

            if (userCancelled) return Result.Cancelled;

            if (session.TargetIds.Count == 0)
            {
                TaskDialog.Show(DuctCfg.Caption, "No duct was selected for alignment.");
                return Result.Cancelled;
            }

            if (!ChooseAxisAuto(doc, session)) return Result.Cancelled;

            return DuctAlignRunner.Run(uidoc, session, ref message);
        }

        /// <summary>X dominates (under 45°) -> equalize Y. Y dominates -> equalize X.</summary>
        private static bool ChooseAxisAuto(Document doc, DuctSession session)
        {
            XYZ c = DuctEngine.PivotCenter(doc, session);
            if (c == null)
            {
                TaskDialog.Show(DuctCfg.Caption, "The pivot geometry could not be read.");
                return false;
            }

            double sx = 0, sy = 0;
            foreach (ElementId id in session.TargetIds)
            {
                XYZ m = DuctUtil.MidPoint(doc.GetElement(id));
                if (m == null) continue;
                sx += Math.Abs(m.X - c.X);
                sy += Math.Abs(m.Y - c.Y);
            }

            session.Axis = sx >= sy ? DuctAxis.EqualizeY : DuctAxis.EqualizeX;
            return true;
        }
    }

    // ------------------------------------------------------------------------------
    //  (b) Align Duct to Connector   (AlignToConnector pattern)
    // ------------------------------------------------------------------------------
    [Transaction(TransactionMode.Manual)]
    public class DuctAlignToConnectorCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            if (DuctAlignRunner.ActiveWindow != null)
            {
                try { DuctAlignRunner.ActiveWindow.Activate(); return Result.Cancelled; }
                catch { DuctAlignRunner.ActiveWindow = null; }
            }

            FamilyInstance fam;
            Connector conn;
            if (!DuctPick.PickFamilyAndConnector(uidoc, doc, out fam, out conn))
                return Result.Cancelled;

            // From the ribbon a new family / connector is chosen: forget the previous duct
            DuctAlignSuite.DuctPalette.ActiveCurveId = ElementId.InvalidElementId;

            return ExecuteWithConnector(uidoc, fam, conn, ref message);
        }

        internal static Result ExecuteWithConnector(UIDocument uidoc, FamilyInstance fam, Connector conn, ref string message)
        {
            Document doc = uidoc.Document;
            ElementId familyId = fam.Id;
            int connectorId = conn.Id;
            XYZ connectorOrigin = conn.Origin;

            if (DuctAlignRunner.ActiveWindow != null)
            {
                try { DuctAlignRunner.ActiveWindow.Activate(); return Result.Cancelled; }
                catch { DuctAlignRunner.ActiveWindow = null; }
            }

            var session = new DuctSession { Document = doc };
            ElementId targetCurveId = ElementId.InvalidElementId;

            // Reuse the remembered duct only if the palette is still locked to THIS connector
            if (DuctAlignSuite.DuctPalette.Window != null
                && DuctAlignSuite.DuctPalette.ActiveFamilyId.Equals(familyId)
                && DuctAlignSuite.DuctPalette.ActiveConnectorId == connectorId
                && !DuctAlignSuite.DuctPalette.ActiveCurveId.Equals(ElementId.InvalidElementId))
            {
                Element existing = doc.GetElement(DuctAlignSuite.DuctPalette.ActiveCurveId);
                if (DuctUtil.IsRigidDuct(existing) && !existing.Id.Equals(familyId))
                    targetCurveId = existing.Id;
            }

            try
            {
                if (targetCurveId.Equals(ElementId.InvalidElementId))
                {
                    Reference tr = uidoc.Selection.PickObject(ObjectType.Element,
                        new DuctSelectionFilter(fam.Id),
                        "Select the rigid duct to align with the connector");
                    targetCurveId = tr.ElementId;
                }
                session.TargetIds.Add(targetCurveId);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            session.PivotId = familyId;
            session.PivotConnectorId = connectorId;
            session.PivotPointOverride = connectorOrigin;
            session.Axis = DuctAxis.Auto;

            return DuctAlignRunner.Run(uidoc, session, ref message);
        }
    }
}
