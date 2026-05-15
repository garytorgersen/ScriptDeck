// One-shot Markdown -> Word (.docx) converter for the ScriptDeck docs.
// Tuned for the patterns actually used in USER_GUIDE.md and USER_MANUAL.md
// rather than aiming for full CommonMark coverage.
//
// Usage:
//   NODE_PATH="<global node_modules>" node md-to-docx.js INPUT.md OUTPUT.docx "Document Title"

const fs = require('fs');
const path = require('path');
const {
    Document, Packer, Paragraph, TextRun, Table, TableRow, TableCell,
    Header, Footer, AlignmentType, LevelFormat, TabStopType, TabStopPosition,
    TableOfContents, HeadingLevel, BorderStyle, WidthType, ShadingType,
    VerticalAlign, PageNumber, PageBreak, ExternalHyperlink, PageOrientation
} = require('docx');

// ---- page geometry (US Letter, 1" margins) ------------------------------
const PAGE_W   = 12240;
const PAGE_H   = 15840;
const MARGIN   = 1440;
const CONTENT_W = PAGE_W - 2 * MARGIN;  // 9360 DXA

// ---- inline parsing ------------------------------------------------------
//
// Returns an array of TextRun / ExternalHyperlink children for one paragraph.
// Handles **bold**, *italic*, _italic_, `code`, [text](url), ![alt](src).
// Image references inside paragraphs become italic placeholder text -- no
// PNG files exist yet for our docs.
//
function parseInline(text) {
    const out = [];

    // Pre-strip image references that sit on their own (block-level
    // images) -- those should never reach this function. Inline image
    // refs are rare in our docs but we handle them anyway.
    const imgRe = /!\[([^\]]*)\]\(([^)]+)\)/g;
    text = text.replace(imgRe, (_, alt) => `__IMG_PLACEHOLDER__${alt}__END_IMG__`);

    // Tokenize by scanning for the next markup boundary. A small state
    // machine is plenty for the patterns we care about.
    let i = 0;
    while (i < text.length) {
        // Hyperlink: [label](url)
        const linkMatch = text.slice(i).match(/^\[([^\]]+)\]\(([^)]+)\)/);
        if (linkMatch) {
            out.push(new ExternalHyperlink({
                children: [new TextRun({ text: linkMatch[1], style: "Hyperlink" })],
                link: linkMatch[2],
            }));
            i += linkMatch[0].length;
            continue;
        }

        // Inline code: `text`
        if (text[i] === '`') {
            const close = text.indexOf('`', i + 1);
            if (close > i) {
                out.push(new TextRun({
                    text: text.slice(i + 1, close),
                    font: "Consolas",
                    size: 20,                 // 10pt
                    shading: { fill: "F4F4F4", type: ShadingType.CLEAR },
                }));
                i = close + 1;
                continue;
            }
        }

        // Bold: **text**
        if (text.slice(i, i + 2) === '**') {
            const close = text.indexOf('**', i + 2);
            if (close > i) {
                // Recurse so nested formatting works (bold + italic etc.)
                const inner = parseInline(text.slice(i + 2, close));
                inner.forEach(r => {
                    if (r instanceof TextRun) {
                        // TextRun is sealed once constructed; rebuild with bold.
                        // We re-read the run's text and font-ish props via internal
                        // option merging by simulating re-creation -- easiest is
                        // to clone-with-bold via the constructor.
                    }
                });
                // Simplest robust path: just emit the inner text with bold.
                // Fancy nesting (bold-italic) we treat as bold only.
                out.push(new TextRun({ text: text.slice(i + 2, close), bold: true }));
                i = close + 2;
                continue;
            }
        }

        // Italic: *text* (single asterisk, not part of **)
        if (text[i] === '*' && text[i + 1] !== '*') {
            const close = text.indexOf('*', i + 1);
            if (close > i) {
                out.push(new TextRun({ text: text.slice(i + 1, close), italics: true }));
                i = close + 1;
                continue;
            }
        }

        // Italic: _text_  (skip if surrounded by alphanumerics on both
        // sides -- snake_case identifiers shouldn't trigger italic)
        if (text[i] === '_') {
            const before = i > 0 ? text[i - 1] : '';
            if (!/[A-Za-z0-9]/.test(before)) {
                const close = text.indexOf('_', i + 1);
                if (close > i) {
                    const after = text[close + 1] || '';
                    if (!/[A-Za-z0-9]/.test(after)) {
                        out.push(new TextRun({ text: text.slice(i + 1, close), italics: true }));
                        i = close + 1;
                        continue;
                    }
                }
            }
        }

        // Image placeholder we stamped earlier
        if (text.slice(i).startsWith('__IMG_PLACEHOLDER__')) {
            const end = text.indexOf('__END_IMG__', i);
            if (end > i) {
                const alt = text.slice(i + '__IMG_PLACEHOLDER__'.length, end);
                out.push(new TextRun({
                    text: `[Screenshot placeholder: ${alt}]`,
                    italics: true,
                    color: "777777",
                }));
                i = end + '__END_IMG__'.length;
                continue;
            }
        }

        // Plain run: consume until the next markup boundary.
        let j = i + 1;
        while (j < text.length) {
            const c = text[j];
            if (c === '`' || c === '[') break;
            if (c === '*') break;
            if (c === '_' && j > 0 && !/[A-Za-z0-9]/.test(text[j - 1])) break;
            if (text.slice(j, j + 19) === '__IMG_PLACEHOLDER__') break;
            j++;
        }
        out.push(new TextRun({ text: text.slice(i, j) }));
        i = j;
    }

    return out;
}

// ---- block parsing -------------------------------------------------------

function parseBlocks(md) {
    // Normalize line endings; we work line-by-line.
    const lines = md.replace(/\r\n/g, '\n').split('\n');
    const blocks = [];
    let i = 0;

    while (i < lines.length) {
        const line = lines[i];

        // Blank line -- skip.
        if (/^\s*$/.test(line)) { i++; continue; }

        // Horizontal rule
        if (/^---+\s*$/.test(line)) {
            blocks.push({ type: 'hr' });
            i++;
            continue;
        }

        // ATX heading
        const h = line.match(/^(#{1,6})\s+(.*?)\s*#*\s*$/);
        if (h) {
            blocks.push({ type: 'heading', level: h[1].length, text: h[2] });
            i++;
            continue;
        }

        // Code fence
        if (/^```/.test(line)) {
            const fence = line.match(/^```(\S*)/);
            const lang = fence[1] || '';
            i++;
            const codeLines = [];
            while (i < lines.length && !/^```/.test(lines[i])) {
                codeLines.push(lines[i]);
                i++;
            }
            i++; // skip closing fence
            blocks.push({ type: 'code', lang, text: codeLines.join('\n') });
            continue;
        }

        // Block-level image (line that's just an image ref)
        const blockImg = line.match(/^!\[([^\]]*)\]\(([^)]+)\)\s*$/);
        if (blockImg) {
            blocks.push({ type: 'image-placeholder', alt: blockImg[1] });
            i++;
            continue;
        }

        // Pipe table: a header row + separator row + data rows
        if (/^\|/.test(line) && i + 1 < lines.length && /^\|?\s*-{2,}/.test(lines[i + 1])) {
            const tableRows = [];
            while (i < lines.length && /^\|/.test(lines[i])) {
                tableRows.push(lines[i]);
                i++;
            }
            // Drop the separator row (index 1) when constructing the table.
            const cells = tableRows.map(splitTableRow);
            const header = cells[0];
            const body = cells.slice(2); // skip separator
            blocks.push({ type: 'table', header, body });
            continue;
        }

        // Unordered list
        if (/^\s*[-*]\s+/.test(line)) {
            const items = [];
            while (i < lines.length && /^\s*[-*]\s+/.test(lines[i])) {
                const m = lines[i].match(/^\s*[-*]\s+(.*)$/);
                let item = m[1];
                // Continuation lines (indented or wrapped) get appended to
                // the current item.
                i++;
                while (i < lines.length && /^\s+\S/.test(lines[i]) && !/^\s*[-*]\s+/.test(lines[i])) {
                    item += ' ' + lines[i].trim();
                    i++;
                }
                items.push(item);
            }
            blocks.push({ type: 'ul', items });
            continue;
        }

        // Ordered list
        if (/^\s*\d+\.\s+/.test(line)) {
            const items = [];
            while (i < lines.length && /^\s*\d+\.\s+/.test(lines[i])) {
                const m = lines[i].match(/^\s*\d+\.\s+(.*)$/);
                let item = m[1];
                i++;
                while (i < lines.length && /^\s+\S/.test(lines[i]) && !/^\s*\d+\.\s+/.test(lines[i])) {
                    item += ' ' + lines[i].trim();
                    i++;
                }
                items.push(item);
            }
            blocks.push({ type: 'ol', items });
            continue;
        }

        // Blockquote -- treat as italic paragraph
        if (/^>\s*/.test(line)) {
            const lines2 = [];
            while (i < lines.length && /^>\s*/.test(lines[i])) {
                lines2.push(lines[i].replace(/^>\s?/, ''));
                i++;
            }
            blocks.push({ type: 'quote', text: lines2.join(' ') });
            continue;
        }

        // Paragraph -- consume continuous non-blank, non-special lines.
        const para = [line];
        i++;
        while (i < lines.length) {
            const l = lines[i];
            if (/^\s*$/.test(l)) break;
            if (/^#{1,6}\s/.test(l)) break;
            if (/^```/.test(l)) break;
            if (/^---+\s*$/.test(l)) break;
            if (/^\s*[-*]\s+/.test(l)) break;
            if (/^\s*\d+\.\s+/.test(l)) break;
            if (/^>\s*/.test(l)) break;
            if (/^\|/.test(l)) break;
            para.push(l);
            i++;
        }
        blocks.push({ type: 'para', text: para.join(' ') });
    }

    return blocks;
}

function splitTableRow(rowText) {
    // Pipe-delimited; strip the leading/trailing pipe + trim each cell.
    let s = rowText.trim();
    if (s.startsWith('|')) s = s.slice(1);
    if (s.endsWith('|')) s = s.slice(0, -1);
    return s.split('|').map(c => c.trim());
}

// ---- block -> docx-js renderers -----------------------------------------

// Markdown # is rare in our docs (title only) so we collapse # and ## both
// to Word Heading 1, then ### -> Heading 2, #### -> Heading 3, etc. This
// matches the conventional translation when authors use `# Title` for the
// doc title and `## Section` as the first major heading.
const HEADING_LEVEL = [
    HeadingLevel.HEADING_1, HeadingLevel.HEADING_1, HeadingLevel.HEADING_1,
    HeadingLevel.HEADING_2, HeadingLevel.HEADING_3, HeadingLevel.HEADING_4,
    HeadingLevel.HEADING_5,
];

function renderHeading(block) {
    return new Paragraph({
        heading: HEADING_LEVEL[block.level] || HeadingLevel.HEADING_3,
        children: parseInline(block.text),
    });
}

function renderParagraph(block) {
    return new Paragraph({
        children: parseInline(block.text),
        spacing: { after: 120 },
    });
}

function renderQuote(block) {
    return new Paragraph({
        children: parseInline(block.text).map(r => {
            if (r instanceof TextRun) {
                // Wrap in italics where possible -- TextRun is sealed but
                // we can re-emit. Simplest path: emit a single italic run.
            }
            return r;
        }),
        spacing: { before: 60, after: 120 },
        indent: { left: 360 },
        // re-emit children with italic explicitly so any plain text gets it
        // (the loop above is conservative; cleaner is to recreate):
    });
}

// More reliable quote rendering: italicize the whole text in one run.
// (parseInline would lose italic-marking on TextRun when nested in **.)
function renderQuoteSimple(block) {
    return new Paragraph({
        children: [new TextRun({ text: block.text, italics: true })],
        spacing: { before: 60, after: 120 },
        indent: { left: 360 },
    });
}

function renderCode(block) {
    // One paragraph per source line so wrapping behaves -- a single
    // gigantic preserved-whitespace run renders awkwardly when it
    // exceeds the page width.
    const lines = block.text.split('\n');
    return lines.map(line => new Paragraph({
        children: [new TextRun({
            text: line || ' ',
            font: "Consolas",
            size: 18,             // 9pt
        })],
        shading: { fill: "F4F4F4", type: ShadingType.CLEAR },
        spacing: { after: 0, line: 240 },
        indent: { left: 240 },
    }));
}

function renderImage(block) {
    return new Paragraph({
        children: [new TextRun({
            text: `[Screenshot placeholder: ${block.alt}]`,
            italics: true,
            color: "777777",
        })],
        spacing: { before: 60, after: 120 },
        alignment: AlignmentType.CENTER,
    });
}

function renderUL(block) {
    return block.items.map(item => new Paragraph({
        children: parseInline(item),
        numbering: { reference: "bullets", level: 0 },
    }));
}

function renderOL(block) {
    return block.items.map(item => new Paragraph({
        children: parseInline(item),
        numbering: { reference: "numbers", level: 0 },
    }));
}

function renderHr() {
    // A paragraph with a bottom border = clean horizontal rule.
    return new Paragraph({
        children: [new TextRun({ text: "" })],
        border: { bottom: { style: BorderStyle.SINGLE, size: 6, color: "CCCCCC", space: 1 } },
        spacing: { before: 60, after: 60 },
    });
}

function renderTable(block) {
    const cols = block.header.length;
    const colWidth = Math.floor(CONTENT_W / cols);
    const widths = new Array(cols).fill(colWidth);
    // Adjust last column to absorb rounding so widths sum exactly.
    widths[cols - 1] = CONTENT_W - colWidth * (cols - 1);

    const border = { style: BorderStyle.SINGLE, size: 4, color: "BBBBBB" };
    const borders = { top: border, bottom: border, left: border, right: border, insideHorizontal: border, insideVertical: border };

    const headerRow = new TableRow({
        tableHeader: true,
        children: block.header.map((cellText, idx) => new TableCell({
            borders,
            width: { size: widths[idx], type: WidthType.DXA },
            shading: { fill: "E5E5E5", type: ShadingType.CLEAR },
            margins: { top: 80, bottom: 80, left: 120, right: 120 },
            children: [new Paragraph({
                children: [new TextRun({ text: cellText, bold: true })],
            })],
        })),
    });

    const bodyRows = block.body.map(row => new TableRow({
        children: row.map((cellText, idx) => new TableCell({
            borders,
            width: { size: widths[idx] || colWidth, type: WidthType.DXA },
            margins: { top: 80, bottom: 80, left: 120, right: 120 },
            children: [new Paragraph({ children: parseInline(cellText) })],
        })),
    }));

    return new Table({
        width: { size: CONTENT_W, type: WidthType.DXA },
        columnWidths: widths,
        rows: [headerRow, ...bodyRows],
    });
}

// ---- main ---------------------------------------------------------------

function buildDoc(title, mdText) {
    const blocks = parseBlocks(mdText);

    // Title page + TOC sit at the very front.
    const today = new Date().toISOString().slice(0, 10);
    const titlePage = [
        new Paragraph({
            children: [new TextRun({ text: title, bold: true, size: 56 })],   // 28pt
            spacing: { before: 2400, after: 240 },
            alignment: AlignmentType.CENTER,
        }),
        new Paragraph({
            children: [new TextRun({ text: "ScriptDeck documentation", size: 28, italics: true, color: "555555" })],
            spacing: { after: 120 },
            alignment: AlignmentType.CENTER,
        }),
        new Paragraph({
            children: [new TextRun({ text: today, size: 24, color: "555555" })],
            spacing: { after: 240 },
            alignment: AlignmentType.CENTER,
        }),
        new Paragraph({ children: [new PageBreak()] }),

        new Paragraph({
            children: [new TextRun({ text: "Table of Contents", bold: true, size: 32 })],
            spacing: { after: 240 },
        }),
        new TableOfContents("Table of Contents", { hyperlink: true, headingStyleRange: "1-3" }),
        new Paragraph({ children: [new PageBreak()] }),
    ];

    // Render every parsed block.
    const body = [];
    for (const b of blocks) {
        switch (b.type) {
            case 'heading':           body.push(renderHeading(b)); break;
            case 'para':              body.push(renderParagraph(b)); break;
            case 'quote':             body.push(renderQuoteSimple(b)); break;
            case 'code':              body.push(...renderCode(b)); break;
            case 'image-placeholder': body.push(renderImage(b)); break;
            case 'ul':                body.push(...renderUL(b)); break;
            case 'ol':                body.push(...renderOL(b)); break;
            case 'hr':                body.push(renderHr()); break;
            case 'table':             body.push(renderTable(b)); break;
        }
    }

    return new Document({
        creator: "ScriptDeck",
        title,
        styles: {
            default: {
                document: { run: { font: "Calibri", size: 22 } },           // 11pt
            },
            paragraphStyles: [
                { id: "Heading1", name: "Heading 1", basedOn: "Normal", next: "Normal", quickFormat: true,
                  run: { size: 36, bold: true, font: "Calibri Light", color: "2E5496" },
                  paragraph: { spacing: { before: 360, after: 180 }, outlineLevel: 0 } },
                { id: "Heading2", name: "Heading 2", basedOn: "Normal", next: "Normal", quickFormat: true,
                  run: { size: 28, bold: true, font: "Calibri Light", color: "2E5496" },
                  paragraph: { spacing: { before: 280, after: 140 }, outlineLevel: 1 } },
                { id: "Heading3", name: "Heading 3", basedOn: "Normal", next: "Normal", quickFormat: true,
                  run: { size: 24, bold: true, font: "Calibri Light", color: "2E5496" },
                  paragraph: { spacing: { before: 200, after: 100 }, outlineLevel: 2 } },
            ],
        },
        numbering: {
            config: [
                { reference: "bullets",
                  levels: [{ level: 0, format: LevelFormat.BULLET, text: "•", alignment: AlignmentType.LEFT,
                    style: { paragraph: { indent: { left: 720, hanging: 360 } } } }] },
                { reference: "numbers",
                  levels: [{ level: 0, format: LevelFormat.DECIMAL, text: "%1.", alignment: AlignmentType.LEFT,
                    style: { paragraph: { indent: { left: 720, hanging: 360 } } } }] },
            ],
        },
        sections: [{
            properties: {
                page: {
                    size: { width: PAGE_W, height: PAGE_H },
                    margin: { top: MARGIN, right: MARGIN, bottom: MARGIN, left: MARGIN },
                },
            },
            footers: {
                default: new Footer({
                    children: [new Paragraph({
                        alignment: AlignmentType.CENTER,
                        children: [
                            new TextRun({ text: "Page ", size: 18, color: "777777" }),
                            new TextRun({ children: [PageNumber.CURRENT], size: 18, color: "777777" }),
                        ],
                    })],
                }),
            },
            children: [...titlePage, ...body],
        }],
    });
}

// ---- CLI entry ----------------------------------------------------------

if (require.main === module) {
    const [,, inputPath, outputPath, title] = process.argv;
    if (!inputPath || !outputPath || !title) {
        console.error("Usage: node md-to-docx.js INPUT.md OUTPUT.docx \"Title\"");
        process.exit(2);
    }
    const md = fs.readFileSync(inputPath, 'utf8');
    const doc = buildDoc(title, md);
    Packer.toBuffer(doc).then(buf => {
        fs.writeFileSync(outputPath, buf);
        console.log(`Wrote ${outputPath} (${buf.length.toLocaleString()} bytes)`);
    }).catch(err => {
        console.error("Pack failed:", err);
        process.exit(1);
    });
}
