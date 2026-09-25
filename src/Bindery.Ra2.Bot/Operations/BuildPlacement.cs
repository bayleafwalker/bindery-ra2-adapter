// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Operations;

/// <summary>
/// Chooses a placement cell for a completed building by spiralling outward
/// from an anchor (the own base) on a fixed grid, skipping cells too close to
/// existing own buildings. When <paramref name="bias"/> is given, candidates
/// within the same ring are preferred by distance to it (toward ore for a
/// refinery, toward the threatened side for a defense), so the outward
/// expansion still favours the useful side of the base.
/// </summary>
internal static class BuildPlacement
{
    public static Cell ChooseCell(
        Cell anchor,
        Cell? bias,
        IReadOnlyList<Cell> avoid,
        int width,
        int height,
        int rings,
        int step)
    {
        for (int ring = 1; ring <= rings; ring++)
        {
            int radius = ring * step;
            List<Cell> candidates = [];
            for (int dx = -radius; dx <= radius; dx += step)
            {
                for (int dy = -radius; dy <= radius; dy += step)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue;
                    Cell candidate = new(anchor.X + dx, anchor.Y + dy);
                    if (candidate.X < 0 || candidate.Y < 0 || candidate.X >= width || candidate.Y >= height) continue;
                    candidates.Add(candidate);
                }
            }

            IEnumerable<Cell> ordered = bias is { } b
                ? candidates.OrderBy(c => c.DistanceTo(b)).ThenBy(c => c.X).ThenBy(c => c.Y)
                : candidates.OrderBy(c => c.X).ThenBy(c => c.Y);

            foreach (Cell candidate in ordered)
            {
                bool blocked = false;
                foreach (Cell used in avoid)
                {
                    if (used.DistanceTo(candidate) < step)
                    {
                        blocked = true;
                        break;
                    }
                }
                if (!blocked) return candidate;
            }
        }

        return anchor;
    }
}
