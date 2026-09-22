using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Glance.Services;

/// <summary>
/// Loads the PDF document outline (table of contents / bookmarks) using PdfSharp.
/// This is read-only and does not contend with the MuPDF rendering engine.
/// </summary>
public sealed class OutlineNode
{
    public string Title { get; set; } = "";
    public int PageIndex { get; set; } = -1; // -1 if destination could not be resolved
    public List<OutlineNode> Children { get; set; } = new();
}

public static class PdfOutlineLoader
{
    public static Task<List<OutlineNode>> LoadAsync(string pdfPath, string? password = null)
    {
        if (string.IsNullOrEmpty(pdfPath) || !File.Exists(pdfPath))
            return Task.FromResult(new List<OutlineNode>());

        return Task.Run(() =>
        {
            PdfDocument? doc = null;
            try
            {
                doc = OpenDocument(pdfPath, password);
                if (doc == null) return new List<OutlineNode>();
                return Walk(doc.Outlines, doc);
            }
            catch
            {
                return new List<OutlineNode>();
            }
            finally
            {
                try { doc?.Close(); } catch { }
            }
        });
    }

    private static PdfDocument? OpenDocument(string path, string? password)
    {
        try
        {
            if (string.IsNullOrEmpty(password))
                return PdfReader.Open(path, PdfDocumentOpenMode.Import);
            return PdfReader.Open(path, password, PdfDocumentOpenMode.Import);
        }
        catch
        {
            return null;
        }
    }

    private static List<OutlineNode> Walk(PdfOutlineCollection outlines, PdfDocument doc)
    {
        var result = new List<OutlineNode>();
        foreach (PdfOutline outline in outlines)
        {
            var node = new OutlineNode
            {
                Title = outline.Title ?? "",
                PageIndex = ResolvePageIndex(outline, doc),
                Children = Walk(outline.Outlines, doc),
            };
            result.Add(node);
        }
        return result;
    }

    private static int ResolvePageIndex(PdfOutline outline, PdfDocument doc)
    {
        try
        {
            if (outline.DestinationPage != null)
            {
                for (int i = 0; i < doc.Pages.Count; i++)
                {
                    if (doc.Pages[i] == outline.DestinationPage) return i;
                }
            }
        }
        catch { }
        return -1;
    }
}
