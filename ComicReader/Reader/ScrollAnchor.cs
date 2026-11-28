using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using ComicReader.Models;

namespace ComicReader.Reader
{
    /// <summary>
    /// Layer 2 helper computing page anchor coordinates inside the viewport
    /// for focal-point zoom preservation and continuous scroll page detection.
    /// </summary>
    public static class ScrollAnchor
    {
        /// <summary>
        /// Finds the realized page container closest to <paramref name="anchorY"/> and the local ratio inside it.
        /// </summary>
        public static (int PageIndex, double RatioInPage) FindZoomAnchor(
            IEnumerable<Control> realizedContainers,
            Visual relativeTo,
            double anchorY,
            int totalPages,
            int currentPage)
        {
            int anchorPageIndex = -1;
            double ratioInPage = 0.0;
            double bestDist = double.MaxValue;

            foreach (Control container in realizedContainers)
            {
                if (container.DataContext is not ComicPageItem item) continue;
                Point? tl = container.TranslatePoint(new Point(0, 0), relativeTo);
                if (!tl.HasValue) continue;

                double h = container.Bounds.Height;
                double top = tl.Value.Y;
                double bottom = top + h;
                double dist = (anchorY >= top && anchorY <= bottom)
                    ? 0.0
                    : Math.Min(Math.Abs(top - anchorY), Math.Abs(bottom - anchorY));

                if (anchorPageIndex == -1 || dist < bestDist)
                {
                    bestDist = dist;
                    anchorPageIndex = item.PageNumber - 1;
                    ratioInPage = h > 0 ? (anchorY - top) / h : 0.0;
                }
            }

            if (anchorPageIndex < 0 && totalPages > 0)
            {
                anchorPageIndex = Math.Clamp(currentPage - 1, 0, totalPages - 1);
            }

            return (anchorPageIndex, ratioInPage);
        }

        /// <summary>
        /// Determines the active 1-based page number at the vertical center of the viewport
        /// and the normalized intra-page offset ratio (0.0 to 1.0).
        /// </summary>
        public static (int Page, double LocalRatio) DetectCurrentPage(
            IEnumerable<Control> realizedContainers,
            Visual relativeTo,
            double viewportHeight,
            int fallbackPage)
        {
            int fallback = Math.Max(1, fallbackPage);
            int best = -1;
            double bestRatio = 0.0;
            double bestDist = double.MaxValue;
            double viewportCenterY = viewportHeight * 0.5;

            foreach (Control container in realizedContainers)
            {
                if (container.DataContext is not ComicPageItem item) continue;
                Point? tl = container.TranslatePoint(new Point(0, 0), relativeTo);
                if (!tl.HasValue) continue;

                double h = container.Bounds.Height;
                double top = tl.Value.Y;
                double bottom = top + h;
                if (bottom < 0 || top > viewportHeight) continue;

                double dist = (viewportCenterY >= top && viewportCenterY <= bottom)
                    ? 0.0
                    : Math.Min(Math.Abs(top - viewportCenterY), Math.Abs(bottom - viewportCenterY));

                if (best == -1 || dist < bestDist)
                {
                    best = item.PageNumber;
                    bestDist = dist;
                    bestRatio = h > 0 ? Math.Clamp(-top / h, 0.0, 1.0) : 0.0;
                }
            }

            return best > 0 ? (best, bestRatio) : (fallback, 0.0);
        }
    }
}
