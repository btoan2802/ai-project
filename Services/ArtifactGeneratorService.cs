using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BtoanAiNet.ViewModels;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;
using P = DocumentFormat.OpenXml.Presentation;
using D = DocumentFormat.OpenXml.Drawing;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace BtoanAiNet.Services
{
    public class ArtifactGeneratorService : IArtifactGeneratorService
    {
        private readonly MarkdownPipeline _pipeline;

        public ArtifactGeneratorService()
        {
            _pipeline = new MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
                .UseSoftlineBreakAsHardlineBreak()
                .Build();
        }

        public async Task<ArtifactExportResultModel> GenerateArtifactAsync(string type, string title, string content, string webRootPath)
        {
            var result = new ArtifactExportResultModel
            {
                Type = (type ?? "docx").ToLowerInvariant().Trim(),
                Success = false
            };

            string safeTitle = SanitizeFilename(string.IsNullOrWhiteSpace(title) ? "Tai_lieu" : title);
            string filesDir = Path.Combine(webRootPath, "files");
            if (!Directory.Exists(filesDir)) Directory.CreateDirectory(filesDir);

            string fileId = $"{safeTitle}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{Guid.NewGuid():N}";

            try
            {
                switch (result.Type)
                {
                    case "docx":
                    case "doc":
                    case "word":
                        result.Type = "docx";
                        result.Filename = $"{fileId}.docx";
                        string docxPath = Path.Combine(filesDir, result.Filename);
                        await GenerateDocxAsync(title, content, docxPath);
                        break;

                    case "pdf":
                        result.Type = "pdf";
                        result.Filename = $"{fileId}.pdf";
                        string pdfPath = Path.Combine(filesDir, result.Filename);
                        GeneratePdf(title, content, pdfPath);
                        break;

                    case "xlsx":
                    case "xls":
                    case "excel":
                        result.Type = "xlsx";
                        result.Filename = $"{fileId}.xlsx";
                        string xlsxPath = Path.Combine(filesDir, result.Filename);
                        GenerateXlsx(title, content, xlsxPath);
                        break;

                    case "pptx":
                    case "ppt":
                    case "powerpoint":
                    case "slide":
                        result.Type = "pptx";
                        result.Filename = $"{fileId}.pptx";
                        string pptxPath = Path.Combine(filesDir, result.Filename);
                        GeneratePptx(title, content, pptxPath);
                        break;

                    case "md":
                        result.Type = "md";
                        result.Filename = $"{fileId}.md";
                        string mdPath = Path.Combine(filesDir, result.Filename);
                        await File.WriteAllTextAsync(mdPath, content, Encoding.UTF8);
                        break;

                    case "html":
                        result.Type = "html";
                        result.Filename = $"{fileId}.html";
                        string htmlPath = Path.Combine(filesDir, result.Filename);
                        string htmlContent = $"<!DOCTYPE html><html><head><meta charset='utf-8'><title>{title}</title><style>body{{font-family:sans-serif;padding:2rem;line-height:1.6;color:#1e293b;max-width:800px;margin:auto;}}table{{border-collapse:collapse;width:100%;margin:1rem 0;}}th,td{{border:1px solid #cbd5e1;padding:8px 12px;text-align:left;}}th{{background:#0F4C5C;color:#fff;}}</style></head><body><h1>{title}</h1>{Markdown.ToHtml(content, _pipeline)}</body></html>";
                        await File.WriteAllTextAsync(htmlPath, htmlContent, Encoding.UTF8);
                        break;

                    default: // txt
                        result.Type = "txt";
                        result.Filename = $"{fileId}.txt";
                        string txtPath = Path.Combine(filesDir, result.Filename);
                        byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF };
                        byte[] textBytes = Encoding.UTF8.GetBytes(content);
                        await File.WriteAllBytesAsync(txtPath, bom.Concat(textBytes).ToArray());
                        break;
                }

                string finalFilePath = Path.Combine(filesDir, result.Filename!);
                if (File.Exists(finalFilePath))
                {
                    var fileInfo = new FileInfo(finalFilePath);
                    result.Size = fileInfo.Length;
                    result.Url = $"/files/{result.Filename}";
                    result.Success = true;
                }
                else
                {
                    result.Error = "File creation failed";
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.Success = false;
            }

            return result;
        }

        public async Task<ArtifactBundleResultModel> GenerateBundleAsync(ArtifactBundleExportRequestModel bundleRequest, string webRootPath)
        {
            var bundleResult = new ArtifactBundleResultModel();
            string filesDir = Path.Combine(webRootPath, "files");
            if (!Directory.Exists(filesDir)) Directory.CreateDirectory(filesDir);

            string safeBundleName = SanitizeFilename(string.IsNullOrWhiteSpace(bundleRequest.BundleName) ? "Btoan_Artifacts" : bundleRequest.BundleName);
            string zipFilename = $"{safeBundleName}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{Guid.NewGuid():N}.zip";
            string zipPath = Path.Combine(filesDir, zipFilename);

            var createdFiles = new List<(string LocalPath, string EntryName)>();

            foreach (var item in bundleRequest.Artifacts)
            {
                // Generate individual artifact if content is provided
                var artResult = await GenerateArtifactAsync(item.Type ?? "docx", item.Title ?? "Tai_lieu", item.Content ?? "", webRootPath);
                bundleResult.Artifacts.Add(artResult);

                if (artResult.Success && !string.IsNullOrEmpty(artResult.Filename))
                {
                    string localPath = Path.Combine(filesDir, artResult.Filename);
                    if (File.Exists(localPath))
                    {
                        string safeItemName = $"{SanitizeFilename(item.Title ?? "File")}.{artResult.Type}";
                        createdFiles.Add((localPath, safeItemName));
                    }
                }
            }

            try
            {
                using (var zipStream = new FileStream(zipPath, FileMode.Create))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
                {
                    var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (localPath, entryName) in createdFiles)
                    {
                        string finalEntry = entryName;
                        int counter = 1;
                        while (usedNames.Contains(finalEntry))
                        {
                            string baseN = Path.GetFileNameWithoutExtension(entryName);
                            string ext = Path.GetExtension(entryName);
                            finalEntry = $"{baseN}_{counter++}{ext}";
                        }
                        usedNames.Add(finalEntry);

                        archive.CreateEntryFromFile(localPath, finalEntry, CompressionLevel.Optimal);
                    }
                }

                bundleResult.Success = true;
                bundleResult.ZipUrl = $"/files/{zipFilename}";
                bundleResult.ZipFilename = zipFilename;
            }
            catch (Exception ex)
            {
                bundleResult.Success = false;
                bundleResult.Error = ex.Message;
            }

            return bundleResult;
        }

        #region Word .docx Generation
        private Task GenerateDocxAsync(string title, string content, string filePath)
        {
            return Task.Run(() =>
            {
                using var wordDoc = WordprocessingDocument.Create(filePath, WordprocessingDocumentType.Document);
                var mainPart = wordDoc.AddMainDocumentPart();
                mainPart.Document = new W.Document(new W.Body());
                var body = mainPart.Document.Body!;

                // 1. Title Paragraph (Document Title)
                string docTitle = string.IsNullOrWhiteSpace(title) ? "Tài liệu Btoan AI" : title;
                var titlePara = body.AppendChild(new W.Paragraph());
                var titlePPr = titlePara.AppendChild(new W.ParagraphProperties());
                titlePPr.AppendChild(new W.Justification { Val = W.JustificationValues.Left });
                titlePPr.AppendChild(new W.SpacingBetweenLines { After = "240", Line = "360", LineRule = W.LineSpacingRuleValues.Auto });

                // Bottom border on Title
                var pBdr = new W.ParagraphBorders();
                pBdr.AppendChild(new W.BottomBorder { Val = W.BorderValues.Single, Size = 12, Space = 4, Color = "0F4C5C" });
                titlePPr.AppendChild(pBdr);

                var titleRun = titlePara.AppendChild(new W.Run(new W.Text(docTitle)));
                titleRun.RunProperties = new W.RunProperties(
                    new W.RunFonts { Ascii = "Segoe UI", HighAnsi = "Segoe UI" },
                    new W.Bold(),
                    new W.Color { Val = "0F4C5C" },
                    new W.FontSize { Val = "36" }
                );

                // Subtitle: Timestamp
                var subPara = body.AppendChild(new W.Paragraph());
                var subRun = subPara.AppendChild(new W.Run(new W.Text($"Xuất bản từ Btoan AI Studio • {DateTime.Now:dd/MM/yyyy HH:mm:ss}")));
                subRun.RunProperties = new W.RunProperties(
                    new W.RunFonts { Ascii = "Segoe UI", HighAnsi = "Segoe UI" },
                    new W.Color { Val = "64748B" },
                    new W.FontSize { Val = "18" },
                    new W.Italic()
                );
                body.AppendChild(new W.Paragraph(new W.ParagraphProperties(new W.SpacingBetweenLines { After = "200" })));

                // 2. Parse Markdown AST
                var document = Markdown.Parse(content, _pipeline);

                foreach (var block in document)
                {
                    if (block is HeadingBlock heading)
                    {
                        var hPara = body.AppendChild(new W.Paragraph());
                        var hPPr = hPara.AppendChild(new W.ParagraphProperties());
                        hPPr.AppendChild(new W.SpacingBetweenLines { Before = "240", After = "120" });

                        string hColor = heading.Level switch
                        {
                            1 => "0F4C5C",
                            2 => "1E3A8A",
                            _ => "334155"
                        };
                        string hSize = heading.Level switch
                        {
                            1 => "30",
                            2 => "24",
                            _ => "20"
                        };

                        if (heading.Inline != null)
                        {
                            AppendInlinesToParagraph(hPara, heading.Inline, hColor, hSize, bold: true);
                        }
                    }
                    else if (block is Markdig.Extensions.Tables.Table table)
                    {
                        var wTable = body.AppendChild(new W.Table());
                        var tblPr = wTable.AppendChild(new W.TableProperties());
                        tblPr.AppendChild(new W.TableBorders(
                            new W.TopBorder { Val = W.BorderValues.Single, Size = 4, Color = "CBD5E1" },
                            new W.BottomBorder { Val = W.BorderValues.Single, Size = 4, Color = "CBD5E1" },
                            new W.LeftBorder { Val = W.BorderValues.Single, Size = 4, Color = "CBD5E1" },
                            new W.RightBorder { Val = W.BorderValues.Single, Size = 4, Color = "CBD5E1" },
                            new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4, Color = "E2E8F0" },
                            new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4, Color = "E2E8F0" }
                        ));

                        bool isHeader = true;
                        foreach (var rowObj in table)
                        {
                            if (rowObj is Markdig.Extensions.Tables.TableRow row)
                            {
                                var wRow = wTable.AppendChild(new W.TableRow());
                                foreach (var cellObj in row)
                                {
                                    if (cellObj is Markdig.Extensions.Tables.TableCell cell)
                                    {
                                        var wCell = wRow.AppendChild(new W.TableCell());
                                        var tcPr = wCell.AppendChild(new W.TableCellProperties());
                                        if (isHeader)
                                        {
                                            tcPr.AppendChild(new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = "0F4C5C" });
                                        }

                                        var cellPara = wCell.AppendChild(new W.Paragraph());
                                        string cellText = "";
                                        foreach (var blockItem in cell)
                                        {
                                            if (blockItem is ParagraphBlock pb && pb.Inline != null)
                                            {
                                                cellText += GetInlinePlainText(pb.Inline);
                                            }
                                        }
                                        var r = cellPara.AppendChild(new W.Run(new W.Text(cellText)));
                                        r.RunProperties = new W.RunProperties(
                                            new W.RunFonts { Ascii = "Segoe UI", HighAnsi = "Segoe UI" },
                                            new W.Color { Val = isHeader ? "FFFFFF" : "1E293B" },
                                            new W.FontSize { Val = "20" }
                                        );
                                        if (isHeader) r.RunProperties.AppendChild(new W.Bold());
                                    }
                                }
                                isHeader = false;
                            }
                        }
                    }
                    else if (block is ListBlock list)
                    {
                        int index = 1;
                        foreach (var itemObj in list)
                        {
                            if (itemObj is ListItemBlock item)
                            {
                                var itemPara = body.AppendChild(new W.Paragraph());
                                var itemPPr = itemPara.AppendChild(new W.ParagraphProperties());
                                itemPPr.AppendChild(new W.Indentation { Left = "400" });
                                itemPPr.AppendChild(new W.SpacingBetweenLines { After = "60" });

                                string prefix = list.IsOrdered ? $"{index++}. " : "• ";
                                var pRun = itemPara.AppendChild(new W.Run(new W.Text(prefix)));
                                pRun.RunProperties = new W.RunProperties(
                                    new W.RunFonts { Ascii = "Segoe UI", HighAnsi = "Segoe UI" },
                                    new W.Bold(),
                                    new W.Color { Val = "0F4C5C" },
                                    new W.FontSize { Val = "22" }
                                );

                                foreach (var subBlock in item)
                                {
                                    if (subBlock is ParagraphBlock pb && pb.Inline != null)
                                    {
                                        AppendInlinesToParagraph(itemPara, pb.Inline, "1E293B", "22");
                                    }
                                }
                            }
                        }
                    }
                    else if (block is QuoteBlock quote)
                    {
                        var qPara = body.AppendChild(new W.Paragraph());
                        var qPPr = qPara.AppendChild(new W.ParagraphProperties());
                        qPPr.AppendChild(new W.Indentation { Left = "600" });
                        qPPr.AppendChild(new W.ParagraphBorders(new W.LeftBorder { Val = W.BorderValues.Single, Size = 12, Space = 8, Color = "0F4C5C" }));
                        qPPr.AppendChild(new W.SpacingBetweenLines { Before = "120", After = "120" });

                        foreach (var subBlock in quote)
                        {
                            if (subBlock is ParagraphBlock pb && pb.Inline != null)
                            {
                                AppendInlinesToParagraph(qPara, pb.Inline, "475569", "20", italic: true);
                            }
                        }
                    }
                    else if (block is FencedCodeBlock code)
                    {
                        var cPara = body.AppendChild(new W.Paragraph());
                        var cPPr = cPara.AppendChild(new W.ParagraphProperties());
                        cPPr.AppendChild(new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = "F1F5F9" });
                        cPPr.AppendChild(new W.Indentation { Left = "300", Right = "300" });

                        string codeText = "";
                        foreach (var line in code.Lines.Lines)
                        {
                            if (line.Slice.Text != null)
                            {
                                codeText += line.Slice.ToString() + "\n";
                            }
                        }
                        var cRun = cPara.AppendChild(new W.Run(new W.Text(codeText)));
                        cRun.RunProperties = new W.RunProperties(
                            new W.RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" },
                            new W.Color { Val = "0F172A" },
                            new W.FontSize { Val = "19" }
                        );
                    }
                    else if (block is ParagraphBlock paragraph && paragraph.Inline != null)
                    {
                        var para = body.AppendChild(new W.Paragraph());
                        var pPr = para.AppendChild(new W.ParagraphProperties());
                        pPr.AppendChild(new W.SpacingBetweenLines { After = "120", Line = "280", LineRule = W.LineSpacingRuleValues.Auto });

                        AppendInlinesToParagraph(para, paragraph.Inline, "1E293B", "22");
                    }
                }

                mainPart.Document.Save();
            });
        }

        private static void AppendInlinesToParagraph(W.Paragraph para, ContainerInline inlines, string defaultColor, string defaultSize, bool bold = false, bool italic = false)
        {
            foreach (var inline in inlines)
            {
                if (inline is LiteralInline lit)
                {
                    var r = para.AppendChild(new W.Run(new W.Text(lit.Content.ToString())));
                    r.RunProperties = new W.RunProperties(
                        new W.RunFonts { Ascii = "Segoe UI", HighAnsi = "Segoe UI" },
                        new W.Color { Val = defaultColor },
                        new W.FontSize { Val = defaultSize }
                    );
                    if (bold) r.RunProperties.AppendChild(new W.Bold());
                    if (italic) r.RunProperties.AppendChild(new W.Italic());
                }
                else if (inline is EmphasisInline emph)
                {
                    bool isBold = emph.DelimiterCount >= 2 || bold;
                    bool isItalic = emph.DelimiterCount % 2 == 1 || italic;
                    foreach (var sub in emph)
                    {
                        if (sub is LiteralInline sLit)
                        {
                            var r = para.AppendChild(new W.Run(new W.Text(sLit.Content.ToString())));
                            r.RunProperties = new W.RunProperties(
                                new W.RunFonts { Ascii = "Segoe UI", HighAnsi = "Segoe UI" },
                                new W.Color { Val = defaultColor },
                                new W.FontSize { Val = defaultSize }
                            );
                            if (isBold) r.RunProperties.AppendChild(new W.Bold());
                            if (isItalic) r.RunProperties.AppendChild(new W.Italic());
                        }
                    }
                }
                else if (inline is LineBreakInline)
                {
                    para.AppendChild(new W.Run(new W.Break()));
                }
                else if (inline is CodeInline codeInl)
                {
                    var r = para.AppendChild(new W.Run(new W.Text(codeInl.Content)));
                    r.RunProperties = new W.RunProperties(
                        new W.RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" },
                        new W.Color { Val = "0F4C5C" },
                        new W.FontSize { Val = defaultSize }
                    );
                }
            }
        }

        private static string GetInlinePlainText(ContainerInline inlines)
        {
            var sb = new StringBuilder();
            foreach (var inl in inlines)
            {
                if (inl is LiteralInline lit) sb.Append(lit.Content.ToString());
                else if (inl is EmphasisInline emph) sb.Append(GetInlinePlainText(emph));
                else if (inl is CodeInline code) sb.Append(code.Content);
                else if (inl is LineBreakInline) sb.Append(" ");
            }
            return sb.ToString();
        }
        #endregion

        #region PDF Generation
        private void GeneratePdf(string title, string content, string filePath)
        {
            QuestPDF.Settings.License = LicenseType.Community;
            string docTitle = string.IsNullOrWhiteSpace(title) ? "Tài liệu Btoan AI" : title;

            var document = QuestPDF.Fluent.Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(2, Unit.Centimetre);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontFamily("Segoe UI").FontSize(10.5f).FontColor("#1E293B"));

                    page.Header().Column(col =>
                    {
                        col.Item().Row(r =>
                        {
                            r.RelativeItem().Text(docTitle).FontSize(16).SemiBold().FontColor("#0F4C5C");
                            r.ConstantItem(120).AlignRight().Text("Btoan AI Studio").FontSize(9).FontColor("#94A3B8");
                        });
                        col.Item().PaddingTop(4).PaddingBottom(12).LineHorizontal(1).LineColor("#E2E8F0");
                    });

                    page.Content().Column(col =>
                    {
                        var mdDoc = Markdown.Parse(content, _pipeline);
                        foreach (var block in mdDoc)
                        {
                            if (block is HeadingBlock heading)
                            {
                                string text = heading.Inline != null ? GetInlinePlainText(heading.Inline) : "";
                                float size = heading.Level switch
                                {
                                    1 => 16f,
                                    2 => 13f,
                                    _ => 11.5f
                                };
                                string color = heading.Level switch
                                {
                                    1 => "#0F4C5C",
                                    2 => "#1E3A8A",
                                    _ => "#334155"
                                };
                                col.Item().PaddingTop(heading.Level == 1 ? 14 : 8).PaddingBottom(4)
                                   .Text(text).FontSize(size).Bold().FontColor(color);
                            }
                            else if (block is Markdig.Extensions.Tables.Table table)
                            {
                                col.Item().PaddingVertical(8).Table(t =>
                                {
                                    var rows = table.OfType<Markdig.Extensions.Tables.TableRow>().ToList();
                                    if (rows.Count > 0)
                                    {
                                        var firstRow = rows[0].OfType<Markdig.Extensions.Tables.TableCell>().ToList();
                                        t.ColumnsDefinition(cDef =>
                                        {
                                            for (int i = 0; i < firstRow.Count; i++) cDef.RelativeColumn();
                                        });

                                        bool isHeader = true;
                                        foreach (var r in rows)
                                        {
                                            var cells = r.OfType<Markdig.Extensions.Tables.TableCell>().ToList();
                                            foreach (var c in cells)
                                            {
                                                string cellText = "";
                                                foreach (var b in c)
                                                {
                                                    if (b is ParagraphBlock pb && pb.Inline != null) cellText += GetInlinePlainText(pb.Inline);
                                                }

                                                var cellItem = t.Cell().Border(0.5f).BorderColor("#CBD5E1");
                                                if (isHeader)
                                                {
                                                    cellItem.Background("#0F4C5C").Padding(6)
                                                        .Text(cellText).FontSize(9.5f).Bold().FontColor(Colors.White);
                                                }
                                                else
                                                {
                                                    cellItem.Padding(5).Text(cellText).FontSize(9f).FontColor("#1E293B");
                                                }
                                            }
                                            isHeader = false;
                                        }
                                    }
                                });
                            }
                            else if (block is ListBlock list)
                            {
                                int idx = 1;
                                foreach (var it in list.OfType<ListItemBlock>())
                                {
                                    string prefix = list.IsOrdered ? $"{idx++}. " : "• ";
                                    foreach (var sub in it.OfType<ParagraphBlock>())
                                    {
                                        if (sub.Inline != null)
                                        {
                                            col.Item().PaddingLeft(12).PaddingBottom(2).Row(r =>
                                            {
                                                r.ConstantItem(18).Text(prefix).Bold().FontColor("#0F4C5C");
                                                r.RelativeItem().Text(GetInlinePlainText(sub.Inline)).FontSize(10f);
                                            });
                                        }
                                    }
                                }
                            }
                            else if (block is ParagraphBlock paragraph && paragraph.Inline != null)
                            {
                                col.Item().PaddingBottom(4).Text(text =>
                                {
                                    foreach (var inl in paragraph.Inline)
                                    {
                                        if (inl is LiteralInline lit) text.Span(lit.Content.ToString()).FontSize(10f);
                                        else if (inl is EmphasisInline emph)
                                        {
                                            var span = text.Span(GetInlinePlainText(emph)).FontSize(10f);
                                            if (emph.DelimiterCount >= 2) span.Bold();
                                            if (emph.DelimiterCount % 2 == 1) span.Italic();
                                        }
                                        else if (inl is LineBreakInline) text.Span("\n");
                                        else if (inl is CodeInline code) text.Span(code.Content).FontFamily("Consolas").BackgroundColor("#F1F5F9");
                                    }
                                });
                            }
                        }
                    });

                    page.Footer().Row(r =>
                    {
                        r.RelativeItem().Text($"Xuất bản: {DateTime.Now:dd/MM/yyyy HH:mm}").FontSize(8.5f).FontColor("#94A3B8");
                        r.ConstantItem(100).AlignRight().Text(x =>
                        {
                            x.DefaultTextStyle(t => t.FontSize(8.5f).FontColor("#64748B"));
                            x.Span("Trang ");
                            x.CurrentPageNumber();
                            x.Span(" / ");
                            x.TotalPages();
                        });
                    });
                });
            });

            document.GeneratePdf(filePath);
        }
        #endregion

        #region Excel .xlsx Generation
        private void GenerateXlsx(string title, string content, string filePath)
        {
            using var workbook = new XLWorkbook();
            string docTitle = string.IsNullOrWhiteSpace(title) ? "BẢNG DỮ LIỆU TỔNG HỢP" : title;

            // Try parsing JSON structure first
            bool parsedFromJson = false;
            try
            {
                var docObj = JsonNode.Parse(content);
                if (docObj is JsonObject jObj)
                {
                    string t = jObj["title"]?.ToString() ?? docTitle;
                    string sub = jObj["subtitle"]?.ToString() ?? $"Ngày lập: {DateTime.Now:dd/MM/yyyy}";
                    var sheets = jObj["sheets"]?.AsArray();

                    if (sheets != null && sheets.Count > 0)
                    {
                        foreach (var sheetNode in sheets)
                        {
                            if (sheetNode is JsonObject sObj)
                            {
                                string sheetName = sObj["name"]?.ToString() ?? "Sheet1";
                                sheetName = SanitizeSheetName(sheetName);
                                var ws = workbook.Worksheets.Add(sheetName);

                                var cols = sObj["columns"]?.AsArray()?.Select(c => c?.ToString() ?? "").ToList() ?? new List<string>();
                                var rows = sObj["rows"]?.AsArray();

                                int totalCols = Math.Max(cols.Count, 1);

                                // Row 1: Merged Title
                                ws.Range(1, 1, 1, totalCols).Merge();
                                var cellTitle = ws.Cell(1, 1);
                                cellTitle.Value = t.ToUpperInvariant();
                                cellTitle.Style.Font.Bold = true;
                                cellTitle.Style.Font.FontSize = 15;
                                cellTitle.Style.Font.FontColor = XLColor.FromArgb(15, 76, 92); // #0F4C5C
                                cellTitle.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                                cellTitle.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                                ws.Row(1).Height = 28;

                                // Row 2: Subtitle
                                ws.Range(2, 1, 2, totalCols).Merge();
                                var cellSub = ws.Cell(2, 1);
                                cellSub.Value = sub;
                                cellSub.Style.Font.Italic = true;
                                cellSub.Style.Font.FontSize = 9.5;
                                cellSub.Style.Font.FontColor = XLColor.FromArgb(100, 116, 139);
                                cellSub.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                                ws.Row(2).Height = 18;

                                // Row 4: Column Headers
                                int headerRow = 4;
                                ws.Row(headerRow).Height = 24;
                                for (int c = 0; c < cols.Count; c++)
                                {
                                    var cell = ws.Cell(headerRow, c + 1);
                                    cell.Value = cols[c];
                                    cell.Style.Font.Bold = true;
                                    cell.Style.Font.FontColor = XLColor.White;
                                    cell.Style.Fill.BackgroundColor = XLColor.FromArgb(15, 76, 92);
                                    cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                                    cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                                    cell.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                                    cell.Style.Border.SetOutsideBorderColor(XLColor.FromArgb(15, 76, 92));
                                }

                                // Data rows
                                int currentRow = headerRow + 1;
                                if (rows != null)
                                {
                                    foreach (var rowNode in rows)
                                    {
                                        ws.Row(currentRow).Height = 20;
                                        bool isZebra = (currentRow % 2 == 0);

                                        if (rowNode is JsonArray rArr)
                                        {
                                            for (int c = 0; c < rArr.Count; c++)
                                            {
                                                var valNode = rArr[c];
                                                var cell = ws.Cell(currentRow, c + 1);
                                                string rawVal = valNode?.ToString() ?? "";

                                                if (rawVal.StartsWith("="))
                                                {
                                                    cell.FormulaA1 = rawVal.Substring(1);
                                                }
                                                else if (double.TryParse(rawVal, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double numVal))
                                                {
                                                    cell.Value = numVal;
                                                    // Center align small integers like STT
                                                    if (c == 0) cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                                                }
                                                else
                                                {
                                                    cell.Value = rawVal;
                                                }

                                                if (isZebra)
                                                {
                                                    cell.Style.Fill.BackgroundColor = XLColor.FromArgb(248, 250, 252);
                                                }
                                                cell.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                                                cell.Style.Border.SetOutsideBorderColor(XLColor.FromArgb(226, 232, 240));
                                            }
                                        }
                                        currentRow++;
                                    }
                                }

                                ws.Columns().AdjustToContents(10.0, 45.0);
                                parsedFromJson = true;
                            }
                        }
                    }
                }
            }
            catch { }

            // If not JSON or failed parsing, parse markdown table or raw text
            if (!parsedFromJson)
            {
                var ws = workbook.Worksheets.Add("BaoCao");
                ws.Cell(1, 1).Value = docTitle.ToUpperInvariant();
                ws.Cell(1, 1).Style.Font.Bold = true;
                ws.Cell(1, 1).Style.Font.FontSize = 14;
                ws.Cell(1, 1).Style.Font.FontColor = XLColor.FromArgb(15, 76, 92);

                int rowIdx = 3;
                var lines = content.Split('\n');
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.Trim().StartsWith("|"))
                    {
                        var parts = line.Split('|', StringSplitOptions.TrimEntries)
                                        .Where((p, idx) => idx > 0 && idx < line.Split('|').Length - 1)
                                        .ToArray();
                        if (parts.Length > 0 && !parts[0].Contains("---"))
                        {
                            for (int colIdx = 0; colIdx < parts.Length; colIdx++)
                            {
                                var cell = ws.Cell(rowIdx, colIdx + 1);
                                string val = parts[colIdx].Trim();
                                if (double.TryParse(val, out double d)) cell.Value = d;
                                else cell.Value = val;
                                cell.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                            }
                            rowIdx++;
                        }
                    }
                    else
                    {
                        ws.Cell(rowIdx, 1).Value = line.Trim();
                        rowIdx++;
                    }
                }
                ws.Columns().AdjustToContents();
            }

            workbook.SaveAs(filePath);
        }

        private static string SanitizeSheetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Sheet1";
            var invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
            foreach (var ch in invalid) name = name.Replace(ch, '_');
            return name.Length > 30 ? name.Substring(0, 30) : name;
        }
        #endregion

        #region PowerPoint .pptx Generation
        private void GeneratePptx(string title, string content, string filePath)
        {
            string docTitle = string.IsNullOrWhiteSpace(title) ? "Btoan AI Presentation" : title;
            string docSubtitle = $"Tạo bởi Btoan AI • {DateTime.Now:dd/MM/yyyy}";
            var slideList = new List<SlideModel>();

            // Try parsing JSON presentation format
            bool parsedFromJson = false;
            try
            {
                var pObj = JsonNode.Parse(content);
                if (pObj is JsonObject jObj)
                {
                    docTitle = jObj["title"]?.ToString() ?? docTitle;
                    docSubtitle = jObj["subtitle"]?.ToString() ?? docSubtitle;
                    var slidesArr = jObj["slides"]?.AsArray();
                    if (slidesArr != null && slidesArr.Count > 0)
                    {
                        foreach (var sNode in slidesArr)
                        {
                            if (sNode is JsonObject sObj)
                            {
                                var sModel = new SlideModel
                                {
                                    Title = sObj["title"]?.ToString() ?? "Nội dung",
                                    Subtitle = sObj["subtitle"]?.ToString()
                                };
                                var bullets = sObj["bullets"]?.AsArray();
                                if (bullets != null)
                                {
                                    foreach (var b in bullets) sModel.Bullets.Add(b?.ToString() ?? "");
                                }
                                else if (!string.IsNullOrEmpty(sObj["content"]?.ToString()))
                                {
                                    sModel.Bullets.Add(sObj["content"]!.ToString());
                                }
                                slideList.Add(sModel);
                            }
                        }
                        parsedFromJson = true;
                    }
                }
            }
            catch { }

            // If not JSON, parse markdown sections as slides
            if (!parsedFromJson || slideList.Count == 0)
            {
                var mdDoc = Markdown.Parse(content, _pipeline);
                SlideModel? currentSlide = null;
                foreach (var b in mdDoc)
                {
                    if (b is HeadingBlock hb)
                    {
                        if (currentSlide != null) slideList.Add(currentSlide);
                        currentSlide = new SlideModel
                        {
                            Title = hb.Inline != null ? GetInlinePlainText(hb.Inline) : "Nội dung"
                        };
                    }
                    else if (b is ListBlock lb && currentSlide != null)
                    {
                        foreach (var it in lb.OfType<ListItemBlock>())
                        {
                            foreach (var pb in it.OfType<ParagraphBlock>())
                            {
                                if (pb.Inline != null) currentSlide.Bullets.Add(GetInlinePlainText(pb.Inline));
                            }
                        }
                    }
                    else if (b is ParagraphBlock pb && currentSlide != null && pb.Inline != null)
                    {
                        currentSlide.Bullets.Add(GetInlinePlainText(pb.Inline));
                    }
                }
                if (currentSlide != null) slideList.Add(currentSlide);

                if (slideList.Count == 0)
                {
                    slideList.Add(new SlideModel { Title = "Tổng quan nội dung", Bullets = new List<string> { content } });
                }
            }

            // Create 16:9 Presentation in OpenXml
            BuildOpenXmlPresentation(filePath, docTitle, docSubtitle, slideList);
        }

        private class SlideModel
        {
            public string Title { get; set; } = "";
            public string? Subtitle { get; set; }
            public List<string> Bullets { get; set; } = new();
        }

        private static void BuildOpenXmlPresentation(string filePath, string title, string subtitle, List<SlideModel> slides)
        {
            using var presentationDoc = PresentationDocument.Create(filePath, PresentationDocumentType.Presentation);
            var presentationPart = presentationDoc.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation();

            // 16:9 Slide Size: 12192000 x 6858000 EMUs
            presentationPart.Presentation.SlideSize = new P.SlideSize { Cx = 12192000, Cy = 6858000, Type = P.SlideSizeValues.Custom };
            presentationPart.Presentation.NotesSize = new P.NotesSize { Cx = 6858000, Cy = 9144000 };

            var slideMasterPart = presentationPart.AddNewPart<SlideMasterPart>("rId1");
            var slideLayoutPart = slideMasterPart.AddNewPart<SlideLayoutPart>("rId1");

            slideLayoutPart.SlideLayout = new P.SlideLayout(new P.CommonSlideData(new P.ShapeTree()));
            slideMasterPart.SlideMaster = new P.SlideMaster(new P.CommonSlideData(new P.ShapeTree()));
            slideMasterPart.SlideMaster.SlideLayoutIdList = new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 2147483649, RelationshipId = "rId1" });
            presentationPart.Presentation.SlideMasterIdList = new P.SlideMasterIdList(new P.SlideMasterId { Id = 2147483648, RelationshipId = "rId1" });

            presentationPart.Presentation.SlideIdList = new P.SlideIdList();

            uint slideId = 256;

            // Slide 1: Modern Navy Title Slide
            AddTitleSlide(presentationPart, slideLayoutPart, slideId++, title, subtitle);

            // Slides 2+: Content Slides
            int slideNum = 2;
            int totalSlides = slides.Count + 1;
            foreach (var s in slides)
            {
                AddContentSlide(presentationPart, slideLayoutPart, slideId++, s.Title, s.Bullets, slideNum++, totalSlides);
            }

            presentationPart.Presentation.Save();
        }

        private static void AddTitleSlide(PresentationPart presPart, SlideLayoutPart layoutPart, uint id, string title, string subtitle)
        {
            var slidePart = presPart.AddNewPart<SlidePart>();
            slidePart.AddPart(layoutPart);

            var shapeTree = new P.ShapeTree();
            shapeTree.AppendChild(new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()));
            shapeTree.AppendChild(new P.GroupShapeProperties(new D.TransformGroup()));

            // Background Shape: Full screen navy blue (#0F4C5C)
            var bgShape = new P.Shape();
            bgShape.AppendChild(new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = 2, Name = "Background" },
                new P.NonVisualShapeDrawingProperties(new D.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties()));
            bgShape.AppendChild(new P.ShapeProperties(
                new D.Transform2D(new D.Offset { X = 0, Y = 0 }, new D.Extents { Cx = 12192000, Cy = 6858000 }),
                new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Rectangle },
                new D.SolidFill(new D.RgbColorModelHex { Val = "0F4C5C" })));
            shapeTree.AppendChild(bgShape);

            // Title Text Shape
            var titleShape = new P.Shape();
            titleShape.AppendChild(new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = 3, Name = "Title" },
                new P.NonVisualShapeDrawingProperties(new D.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties()));
            titleShape.AppendChild(new P.ShapeProperties(
                new D.Transform2D(new D.Offset { X = 1200000, Y = 2200000 }, new D.Extents { Cx = 9792000, Cy = 1800000 }),
                new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Rectangle },
                new D.NoFill()));

            var tb = new P.TextBody(
                new D.BodyProperties(),
                new D.ListStyle(),
                new D.Paragraph(
                    new D.ParagraphProperties { Alignment = D.TextAlignmentTypeValues.Center },
                    new D.Run(
                        new D.RunProperties { Language = "vi-VN", FontSize = 4400, Bold = true }
                            .AppendChildItem(new D.SolidFill(new D.RgbColorModelHex { Val = "FFFFFF" }))
                            .AppendChildItem(new D.LatinFont { Typeface = "Segoe UI" }),
                        new D.Text(title)
                    )
                ),
                new D.Paragraph(
                    new D.ParagraphProperties { Alignment = D.TextAlignmentTypeValues.Center },
                    new D.Run(
                        new D.RunProperties { Language = "vi-VN", FontSize = 2200, Italic = true }
                            .AppendChildItem(new D.SolidFill(new D.RgbColorModelHex { Val = "94A3B8" }))
                            .AppendChildItem(new D.LatinFont { Typeface = "Segoe UI" }),
                        new D.Text(subtitle)
                    )
                )
            );
            titleShape.AppendChild(tb);
            shapeTree.AppendChild(titleShape);

            slidePart.Slide = new P.Slide(new P.CommonSlideData(shapeTree));
            if (presPart.Presentation?.SlideIdList != null)
            {
                presPart.Presentation.SlideIdList.AppendChild(new P.SlideId { Id = id, RelationshipId = presPart.GetIdOfPart(slidePart) });
            }
        }

        private static void AddContentSlide(PresentationPart presPart, SlideLayoutPart layoutPart, uint id, string title, List<string> bullets, int slideNum, int totalSlides)
        {
            var slidePart = presPart.AddNewPart<SlidePart>();
            slidePart.AddPart(layoutPart);

            var shapeTree = new P.ShapeTree();
            shapeTree.AppendChild(new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()));
            shapeTree.AppendChild(new P.GroupShapeProperties(new D.TransformGroup()));

            // Header Banner: Top bar in navy (#0F4C5C)
            var bannerShape = new P.Shape();
            bannerShape.AppendChild(new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = 2, Name = "HeaderBanner" },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()));
            bannerShape.AppendChild(new P.ShapeProperties(
                new D.Transform2D(new D.Offset { X = 800000, Y = 600000 }, new D.Extents { Cx = 10592000, Cy = 900000 }),
                new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Rectangle },
                new D.NoFill()));

            var bannerTb = new P.TextBody(
                new D.BodyProperties(),
                new D.ListStyle(),
                new D.Paragraph(
                    new D.ParagraphProperties { Alignment = D.TextAlignmentTypeValues.Left },
                    new D.Run(
                        new D.RunProperties { Language = "vi-VN", FontSize = 3200, Bold = true }
                            .AppendChildItem(new D.SolidFill(new D.RgbColorModelHex { Val = "0F4C5C" }))
                            .AppendChildItem(new D.LatinFont { Typeface = "Segoe UI" }),
                        new D.Text(title)
                    )
                )
            );
            bannerShape.AppendChild(bannerTb);
            shapeTree.AppendChild(bannerShape);

            // Divider Line
            var lineShape = new P.Shape();
            lineShape.AppendChild(new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = 3, Name = "Divider" },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()));
            lineShape.AppendChild(new P.ShapeProperties(
                new D.Transform2D(new D.Offset { X = 800000, Y = 1500000 }, new D.Extents { Cx = 10592000, Cy = 20000 }),
                new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Rectangle },
                new D.SolidFill(new D.RgbColorModelHex { Val = "E2E8F0" })));
            shapeTree.AppendChild(lineShape);

            // Content Bullets Shape
            var contentShape = new P.Shape();
            contentShape.AppendChild(new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = 4, Name = "Bullets" },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()));
            contentShape.AppendChild(new P.ShapeProperties(
                new D.Transform2D(new D.Offset { X = 800000, Y = 1800000 }, new D.Extents { Cx = 10592000, Cy = 4200000 }),
                new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Rectangle },
                new D.NoFill()));

            var contentTb = new P.TextBody(new D.BodyProperties(), new D.ListStyle());
            foreach (var b in bullets)
            {
                var p = new D.Paragraph(
                    new D.ParagraphProperties { LeftMargin = 280000, Indent = -280000 },
                    new D.Run(
                        new D.RunProperties { Language = "vi-VN", FontSize = 2200 }
                            .AppendChildItem(new D.SolidFill(new D.RgbColorModelHex { Val = "1E293B" }))
                            .AppendChildItem(new D.LatinFont { Typeface = "Segoe UI" }),
                        new D.Text($"•   {b}")
                    )
                );
                contentTb.AppendChild(p);
            }
            contentShape.AppendChild(contentTb);
            shapeTree.AppendChild(contentShape);

            // Footer / Slide Number
            var footerShape = new P.Shape();
            footerShape.AppendChild(new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = 5, Name = "Footer" },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()));
            footerShape.AppendChild(new P.ShapeProperties(
                new D.Transform2D(new D.Offset { X = 800000, Y = 6200000 }, new D.Extents { Cx = 10592000, Cy = 400000 }),
                new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Rectangle },
                new D.NoFill()));

            var footerTb = new P.TextBody(
                new D.BodyProperties(),
                new D.ListStyle(),
                new D.Paragraph(
                    new D.ParagraphProperties { Alignment = D.TextAlignmentTypeValues.Right },
                    new D.Run(
                        new D.RunProperties { Language = "vi-VN", FontSize = 1400 }
                            .AppendChildItem(new D.SolidFill(new D.RgbColorModelHex { Val = "94A3B8" }))
                            .AppendChildItem(new D.LatinFont { Typeface = "Segoe UI" }),
                        new D.Text($"{slideNum} / {totalSlides}")
                    )
                )
            );
            footerShape.AppendChild(footerTb);
            shapeTree.AppendChild(footerShape);

            slidePart.Slide = new P.Slide(new P.CommonSlideData(shapeTree));
            if (presPart.Presentation?.SlideIdList != null)
            {
                presPart.Presentation.SlideIdList.AppendChild(new P.SlideId { Id = id, RelationshipId = presPart.GetIdOfPart(slidePart) });
            }
        }
        #endregion

        private static string SanitizeFilename(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "File";
            string normalized = name.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char c in normalized)
            {
                var unicodeCategory = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    if (char.IsLetterOrDigit(c)) sb.Append(c);
                    else if (c == ' ' || c == '_' || c == '-') sb.Append('_');
                }
            }
            string cleaned = sb.ToString().Trim('_');
            return string.IsNullOrEmpty(cleaned) ? "File" : (cleaned.Length > 30 ? cleaned.Substring(0, 30) : cleaned);
        }
    }

    // Helper extension method to append children fluently
    internal static class OpenXmlElementExtensions
    {
        public static T AppendChildItem<T>(this T parent, OpenXmlElement child) where T : OpenXmlElement
        {
            parent.AppendChild(child);
            return parent;
        }
    }
}
