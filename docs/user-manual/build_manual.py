from pathlib import Path

from reportlab.lib import colors
from reportlab.lib.colors import HexColor
from reportlab.lib.enums import TA_CENTER, TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.units import inch, mm
from reportlab.platypus import (
    BaseDocTemplate,
    Frame,
    PageTemplate,
    Paragraph,
    Spacer,
    Table,
    TableStyle,
    PageBreak,
    Image,
    Flowable,
    KeepTogether,
)


MANUAL_DIR = Path(__file__).resolve().parent
OUT = MANUAL_DIR / "native-fractal-flame-curator-user-manual.pdf"
SCREENSHOT = MANUAL_DIR / "app-main-clean.png"

PAGE_W, PAGE_H = A4
NAVY = HexColor("#101923")
INK = HexColor("#1A2633")
SLATE = HexColor("#475569")
MUTED = HexColor("#64748B")
TEAL = HexColor("#159A9C")
TEAL_LIGHT = HexColor("#DDF5F1")
ORANGE = HexColor("#D97706")
ORANGE_LIGHT = HexColor("#FFF1D6")
RED = HexColor("#C2413B")
RED_LIGHT = HexColor("#FCE8E7")
GREEN = HexColor("#2E7D5B")
GREEN_LIGHT = HexColor("#E3F3EA")
LINE = HexColor("#D8E0E8")
PALE = HexColor("#F5F8FA")


styles = getSampleStyleSheet()
styles.add(ParagraphStyle(
    name="ManualTitle", parent=styles["Title"], fontName="Helvetica-Bold",
    fontSize=29, leading=34, textColor=NAVY, spaceAfter=8,
))
styles.add(ParagraphStyle(
    name="Subtitle", parent=styles["Normal"], fontName="Helvetica",
    fontSize=12, leading=16, textColor=TEAL, spaceAfter=16,
))
styles.add(ParagraphStyle(
    name="Lead", parent=styles["BodyText"], fontName="Helvetica",
    fontSize=11.2, leading=16, textColor=INK, spaceAfter=10,
))
styles.add(ParagraphStyle(
    name="Body", parent=styles["BodyText"], fontName="Helvetica",
    fontSize=9.2, leading=13.2, textColor=INK, spaceAfter=6,
))
styles.add(ParagraphStyle(
    name="Small", parent=styles["BodyText"], fontName="Helvetica",
    fontSize=7.8, leading=10.3, textColor=SLATE, spaceAfter=3,
))
styles.add(ParagraphStyle(
    name="Section", parent=styles["Heading1"], fontName="Helvetica-Bold",
    fontSize=19, leading=23, textColor=NAVY, spaceBefore=0, spaceAfter=9,
))
styles.add(ParagraphStyle(
    name="Subsection", parent=styles["Heading2"], fontName="Helvetica-Bold",
    fontSize=11.5, leading=14, textColor=TEAL, spaceBefore=5, spaceAfter=5,
))
styles.add(ParagraphStyle(
    name="CardTitle", parent=styles["Normal"], fontName="Helvetica-Bold",
    fontSize=10, leading=12, textColor=NAVY, spaceAfter=3,
))
styles.add(ParagraphStyle(
    name="CardBody", parent=styles["Normal"], fontName="Helvetica",
    fontSize=8.1, leading=10.6, textColor=SLATE,
))
styles.add(ParagraphStyle(
    name="TableHead", parent=styles["Normal"], fontName="Helvetica-Bold",
    fontSize=8.2, leading=10, textColor=colors.white,
))
styles.add(ParagraphStyle(
    name="TableCell", parent=styles["Normal"], fontName="Helvetica",
    fontSize=7.7, leading=10, textColor=INK,
))
styles.add(ParagraphStyle(
    name="TableCellBold", parent=styles["Normal"], fontName="Helvetica-Bold",
    fontSize=7.7, leading=10, textColor=NAVY,
))
styles.add(ParagraphStyle(
    name="Quote", parent=styles["BodyText"], fontName="Helvetica-Oblique",
    fontSize=10, leading=14, textColor=NAVY, leftIndent=10, rightIndent=10,
    spaceBefore=2, spaceAfter=2,
))


def P(text, style="Body"):
    return Paragraph(text, styles[style])


def bullet(text):
    return Paragraph("<font color='#159A9C'>-</font> " + text, styles["Body"])


class FlowDiagram(Flowable):
    def __init__(self, width, height=113):
        super().__init__()
        self.width = width
        self.height = height

    def draw(self):
        c = self.canv
        labels = [
            ("Explore", "render or import"),
            ("Rank", "optional AI score"),
            ("Review", "human sees one image"),
            ("Teach", "rate 1 to 5"),
            ("Refine", "retrain and repeat"),
        ]
        gap = 7
        box_w = (self.width - gap * 4) / 5
        y = 35
        for i, (title, detail) in enumerate(labels):
            x = i * (box_w + gap)
            fill = TEAL_LIGHT if i in (0, 1) else (ORANGE_LIGHT if i == 3 else PALE)
            c.setFillColor(fill)
            c.setStrokeColor(TEAL if i == 1 else LINE)
            c.setLineWidth(1.2)
            c.roundRect(x, y, box_w, 47, 7, fill=1, stroke=1)
            c.setFillColor(NAVY)
            c.setFont("Helvetica-Bold", 8.4)
            c.drawCentredString(x + box_w / 2, y + 29, title)
            c.setFillColor(SLATE)
            c.setFont("Helvetica", 6.5)
            c.drawCentredString(x + box_w / 2, y + 16, detail)
            if i < 4:
                c.setStrokeColor(TEAL)
                c.setLineWidth(1.2)
                x1 = x + box_w + 1
                x2 = x + box_w + gap - 2
                cy = y + 23.5
                c.line(x1, cy, x2, cy)
                c.line(x2 - 3, cy + 3, x2, cy)
                c.line(x2 - 3, cy - 3, x2, cy)
        c.setFillColor(MUTED)
        c.setFont("Helvetica-Oblique", 7.4)
        c.drawCentredString(self.width / 2, 15, "The human rating folders are the source of truth; the AI only helps order candidates.")


class OrdinalDiagram(Flowable):
    def __init__(self, width, height=83):
        super().__init__()
        self.width = width
        self.height = height

    def draw(self):
        c = self.canv
        boxes = [
            ("4 thresholds", ">=2  >=3  >=4  >=5", TEAL_LIGHT),
            ("Expected rating", "1.0 to 5.0", ORANGE_LIGHT),
            ("Runtime score", "0.0 to 1.0", GREEN_LIGHT),
        ]
        gap = 13
        bw = (self.width - gap * 2) / 3
        y = 19
        for i, (head, detail, fill) in enumerate(boxes):
            x = i * (bw + gap)
            c.setFillColor(fill)
            c.setStrokeColor(LINE)
            c.roundRect(x, y, bw, 39, 6, fill=1, stroke=1)
            c.setFillColor(NAVY)
            c.setFont("Helvetica-Bold", 8.1)
            c.drawCentredString(x + bw / 2, y + 24, head)
            c.setFillColor(SLATE)
            c.setFont("Helvetica", 7.3)
            c.drawCentredString(x + bw / 2, y + 11, detail)
            if i < 2:
                c.setStrokeColor(TEAL)
                c.setLineWidth(1.2)
                x1, x2, cy = x + bw + 2, x + bw + gap - 3, y + 19.5
                c.line(x1, cy, x2, cy)
                c.line(x2 - 3, cy + 3, x2, cy)
                c.line(x2 - 3, cy - 3, x2, cy)


class FolderDiagram(Flowable):
    def __init__(self, width, height=136):
        super().__init__()
        self.width = width
        self.height = height

    def draw(self):
        c = self.canv
        c.setFillColor(PALE)
        c.setStrokeColor(LINE)
        c.roundRect(5, 6, self.width - 10, self.height - 12, 8, fill=1, stroke=1)
        c.setFillColor(NAVY)
        c.setFont("Helvetica-Bold", 9)
        c.drawString(17, self.height - 25, "Workspace output")
        rows = [
            ("rendered/", "unrated complete PNG + .flame pairs", TEAL),
            ("ratings/1 ... ratings/5/", "human labels; AI never moves files here", ORANGE),
            ("controls/", "evaluation-only images; not training labels", MUTED),
            ("models/", "stored preference model versions", MUTED),
        ]
        y = self.height - 42
        for name, desc, color in rows:
            c.setFillColor(color)
            c.setFont("Helvetica-Bold", 8.3)
            c.drawString(22, y, "|-- " + name)
            c.setFillColor(SLATE)
            c.setFont("Helvetica", 7.8)
            c.drawString(128, y, desc)
            y -= 20
        c.setFillColor(SLATE)
        c.setFont("Helvetica", 7.5)
        c.drawString(22, 10, "A stable source ID stays with the .flame genome even when a score prefix changes.")


class ReadinessGraphic(Flowable):
    def __init__(self, width, height=80):
        super().__init__()
        self.width = width
        self.height = height

    def draw(self):
        c = self.canv
        c.setFillColor(NAVY)
        c.setFont("Helvetica-Bold", 8.5)
        c.drawString(0, self.height - 12, "Live dataset chart in the app")
        labels = [("1", RED), ("2", ORANGE), ("3", GREEN), ("4", GREEN), ("5", GREEN)]
        base = 24
        bar_w = 29
        gap = 19
        for i, (label, color) in enumerate(labels):
            x = 17 + i * (bar_w + gap)
            heights = [18, 31, 45, 57, 37]
            c.setFillColor(HexColor("#E8EDF1"))
            c.roundRect(x, base, bar_w, 45, 3, fill=1, stroke=0)
            c.setFillColor(color)
            c.roundRect(x, base, bar_w, heights[i], 3, fill=1, stroke=0)
            c.setFillColor(NAVY)
            c.setFont("Helvetica-Bold", 8)
            c.drawCentredString(x + bar_w / 2, 11, label)
        c.setFillColor(RED)
        c.circle(self.width - 178, 48, 4, fill=1, stroke=0)
        c.setFillColor(SLATE)
        c.setFont("Helvetica", 7.2)
        c.drawString(self.width - 168, 45, "very small or missing")
        c.setFillColor(ORANGE)
        c.circle(self.width - 178, 31, 4, fill=1, stroke=0)
        c.setFillColor(SLATE)
        c.drawString(self.width - 168, 28, "usable but weak")
        c.setFillColor(GREEN)
        c.circle(self.width - 178, 14, 4, fill=1, stroke=0)
        c.setFillColor(SLATE)
        c.drawString(self.width - 168, 11, "reasonably balanced")


def callout(text, fill=TEAL_LIGHT, border=TEAL, style="Body"):
    t = Table([[P(text, style)]], colWidths=[None])
    t.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, -1), fill),
        ("BOX", (0, 0), (-1, -1), 0.8, border),
        ("LEFTPADDING", (0, 0), (-1, -1), 10),
        ("RIGHTPADDING", (0, 0), (-1, -1), 10),
        ("TOPPADDING", (0, 0), (-1, -1), 7),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 7),
    ]))
    return t


def cards(items, width):
    data = [[P(title, "CardTitle") for title, _ in items], [P(body, "CardBody") for _, body in items]]
    t = Table(data, colWidths=[width / len(items)] * len(items))
    t.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, -1), PALE),
        ("BOX", (0, 0), (-1, -1), 0.7, LINE),
        ("INNERGRID", (0, 0), (-1, -1), 0.7, LINE),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("LEFTPADDING", (0, 0), (-1, -1), 9),
        ("RIGHTPADDING", (0, 0), (-1, -1), 9),
        ("TOPPADDING", (0, 0), (-1, -1), 8),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 8),
    ]))
    return t


def table(rows, widths, header=True, compact=False):
    converted = []
    for r, row in enumerate(rows):
        converted.append([
            item if isinstance(item, Paragraph) else P(str(item), "TableHead" if header and r == 0 else ("TableCellBold" if c == 0 else "TableCell"))
            for c, item in enumerate(row)
        ])
    t = Table(converted, colWidths=widths, repeatRows=1 if header else 0, hAlign="LEFT")
    style = [
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("GRID", (0, 0), (-1, -1), 0.45, LINE),
        ("LEFTPADDING", (0, 0), (-1, -1), 6 if compact else 7),
        ("RIGHTPADDING", (0, 0), (-1, -1), 6 if compact else 7),
        ("TOPPADDING", (0, 0), (-1, -1), 5 if compact else 6),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 5 if compact else 6),
    ]
    if header:
        style += [("BACKGROUND", (0, 0), (-1, 0), NAVY), ("TEXTCOLOR", (0, 0), (-1, 0), colors.white)]
        for i in range(1, len(rows)):
            if i % 2 == 0:
                style.append(("BACKGROUND", (0, i), (-1, i), PALE))
    t.setStyle(TableStyle(style))
    return t


def page_chrome(canvas, doc):
    canvas.saveState()
    canvas.setFillColor(NAVY)
    canvas.rect(0, PAGE_H - 7 * mm, PAGE_W, 7 * mm, fill=1, stroke=0)
    canvas.setFillColor(TEAL)
    canvas.rect(0, PAGE_H - 7 * mm, 48 * mm, 7 * mm, fill=1, stroke=0)
    canvas.setFillColor(MUTED)
    canvas.setFont("Helvetica", 7.5)
    canvas.drawString(17 * mm, 11 * mm, "Native Fractal-Flame Curator | User manual")
    canvas.drawRightString(PAGE_W - 17 * mm, 11 * mm, f"{doc.page}")
    canvas.restoreState()


class ManualDocTemplate(BaseDocTemplate):
    def __init__(self, filename, **kwargs):
        super().__init__(filename, **kwargs)
        frame = Frame(self.leftMargin, self.bottomMargin, self.width, self.height, id="normal")
        self.addPageTemplates([PageTemplate(id="manual", frames=[frame], onPage=page_chrome)])


doc = ManualDocTemplate(
    str(OUT), pagesize=A4,
    leftMargin=17 * mm, rightMargin=17 * mm,
    topMargin=18 * mm, bottomMargin=18 * mm,
    title="Native Fractal-Flame Curator User Manual",
    author="Native Fractal-Flame Curator",
)

W = doc.width
story = []

# Cover
story.append(Spacer(1, 18 * mm))
story.append(P("NATIVE FRACTAL-FLAME<br/>CURATOR", "ManualTitle"))
story.append(P("A practical guide to exploring, learning, and refining your visual taste", "Subtitle"))
story.append(P(
    "Native Fractal-Flame Curator is a personal visual preference laboratory. It generates reproducible Apophysis-compatible fractal genomes, renders them locally, and lets you teach an optional AI scorer what you like through simple human ratings.",
    "Lead",
))
story.append(callout(
    "The central idea: <b>you decide what is good</b>. The AI estimates your learned preference and helps order the queue, but it never accepts, deletes, hides, moves, or rates a candidate for you.",
    fill=TEAL_LIGHT, border=TEAL, style="Lead",
))
story.append(Spacer(1, 10))
story.append(cards([
    ("EXPLORE", "Render seeded random flames or bring in your own compatible source pairs."),
    ("TEACH", "Use the five star buttons to create an ordinal record of your taste."),
    ("REFINE", "Train, inspect, correct, and repeat until the search becomes more personal."),
], W))
story.append(Spacer(1, 13))
if SCREENSHOT.exists():
    story.append(Image(str(SCREENSHOT), width=W, height=W * 920 / 1540))
    story.append(Spacer(1, 4))
    story.append(P("The application keeps rendering and AI scoring independently controllable. Sections can be collapsed to keep the workspace calm.", "Small"))
story.append(Spacer(1, 5))
story.append(P("Phase 1 provides deterministic rendering and manual curation. Phase 2 adds an optional CUDA-only DINOv2 preference scorer while preserving that manual workflow as the source of truth.", "Small"))
story.append(PageBreak())

# Core idea
story.append(P("1. The core idea", "Section"))
story.append(P("Use the program as a loop: generate possibilities, let the optional model put likely favorites first, then correct it with your own ratings.", "Lead"))
story.append(FlowDiagram(W))
story.append(Spacer(1, 5))
story.append(P("What the score means", "Subsection"))
story.append(P("The AI score is a continuous estimate from 0 to 1 of predicted user preference. It is not a DINOv2-native beauty score, an objective quality score, or an automatic acceptance decision. Low-scoring candidates remain available and can still be reached with Previous and Next.", "Body"))
story.append(OrdinalDiagram(W))
story.append(P("DINOv2 ViT-B/14 is used as a frozen visual feature extractor. A small trainable ordinal head learns four cumulative questions: rating at least 2, 3, 4, and 5. Those probabilities become an expected rating from 1 to 5, then map to the 0 to 1 runtime score.", "Small"))
story.append(P("Image preparation", "Subsection"))
story.append(P("Large source renders are valuable because they preserve detail before inference. The current scorer converts monochrome images to RGB, resizes the short side to 256 pixels, center-crops to 224 x 224, and applies the official ImageNet normalization used by DINOv2. Training and scoring use the same preprocessing.", "Body"))
story.append(callout("Rendering, manual rating, and browsing remain usable when CUDA is unavailable. AI scoring and training are clearly disabled or warned about rather than silently pretending to run on CPU.", fill=ORANGE_LIGHT, border=ORANGE, style="Body"))
story.append(Spacer(1, 6))
story.append(P("A simple mental model", "Subsection"))
story.append(cards([
    ("Renderer", "Creates candidate source pairs and keeps the UI responsive during continuous work."),
    ("Curator", "You inspect one image at a time and assign the five ordinal labels."),
    ("Scorer", "The optional model ranks candidates according to the labels you supplied."),
], W))
story.append(PageBreak())

# Quick start and file ownership
story.append(P("2. Quick start", "Section"))
story.append(P("A first session can be as small as a handful of examples. Training is allowed with any dataset size, but the app will warn when validation and test metrics are not reliable.", "Lead"))
quick = [
    ("1", "Choose an output directory and a base random seed. A seed is saved with each source genome so an exploration can be reproduced."),
    ("2", "Press Start in Rendering. Set the session limit, workers, queue depth, and quality controls as needed."),
    ("3", "Build a human-labelled starting set by rating rendered images 1 to 5, or by placing images you already understand into the matching ratings folder."),
    ("4", "Press Train Model. The current ratings are snapshotted, the frozen backbone is used for features, and the new ordinal head is validated and activated only after successful training."),
    ("5", "Press Start AI scoring. Existing complete candidates are scanned first; newly completed candidate pairs are then watched and scored in the background."),
    ("6", "Review the best-first candidate, but use Previous and Next to inspect every candidate. Rate, undo, and retrain whenever your taste record improves."),
]
quick_rows = [["Step", "What to do"]] + quick
story.append(table(quick_rows, [13 * mm, W - 13 * mm], compact=True))
story.append(Spacer(1, 9))
story.append(P("Adding your own images", "Subsection"))
story.append(P("There are two clean routes:", "Body"))
story.append(bullet("For the unreviewed candidate queue, add a PNG and its matching .flame genome to rendered/. The pair should share the same stable source ID. The scorer treats an incomplete pair as pending rather than guessing."))
story.append(bullet("For a known training example, place the image in ratings/1, ratings/2, ratings/3, ratings/4, or ratings/5 according to your own judgement. The AI never chooses the folder and never moves files between rating folders."))
story.append(bullet("Controls such as black, sparse, clipped, low-detail, and non-fractal images are for evaluation only unless you explicitly rate them. Do not automatically turn black images into bad labels."))
story.append(callout("Keep the source .flame with the rendered PNG whenever possible. This preserves the link back to the genome and makes re-rendering and stable source grouping safe.", fill=GREEN_LIGHT, border=GREEN, style="Body"))
story.append(Spacer(1, 7))
story.append(P("3. Files and ownership", "Section"))
story.append(FolderDiagram(W))
story.append(P("The score prefix is fixed width from 000000 through 100000, for example 087342__flame_000142.png. When a user rates an image, the PNG and matching .flame move together into the selected star folder and the AI prefix is removed from the destination name. The original source ID is retained.", "Body"))
story.append(P("AI rescoring may rename the score prefix on rendered candidates and their matching .flame files. It never renames, deletes, modifies, or reclassifies files inside ratings/1 through ratings/5.", "Body"))
story.append(PageBreak())

# Controls page
story.append(P("4. Controls", "Section"))
story.append(P("The left menu is intentionally small. Expand only the section you need; the viewport remains the single place where you inspect and rate an image.", "Lead"))
story.append(P("Rendering", "Subsection"))
render_rows = [
    ["Control", "Purpose"],
    ["Output directory", "Workspace root for rendered candidates, ratings, source archives, and model artifacts."],
    ["Base random seed", "Starting seed for reproducible genome generation."],
    ["Session limit", "Stops a finite render batch after the requested number of sources."],
    ["Workers", "Number of bounded background render workers."],
    ["Bounded render queue", "Limits pending work so overnight rendering remains controlled."],
    ["Start / Stop", "Starts a new render session or stops the active session."],
    ["Pause / Resume", "Temporarily holds rendering without losing the session."],
    ["Queue and progress", "Shows pending work, completed work, and session state."],
]
story.append(table(render_rows, [42 * mm, W - 42 * mm], compact=True))
story.append(Spacer(1, 7))
story.append(P("Image settings drawer", "Subsection"))
image_rows = [
    ["Control", "Purpose"],
    ["Sample budget", "Controls the number of samples used by the built-in render."],
    ["Oversample", "Internal render multiplier before downsampling; it is not a hidden quality claim."],
    ["Filter radius", "Controls filtering during downsampling."],
    ["Gamma, brightness, vibrancy", "Tone and color intensity adjustments."],
    ["White point, black point, contrast curve", "Maps rendered density into the displayed tonal range."],
    ["Low-density cutoff", "Suppresses very faint density when desired."],
    ["Palette", "Monochrome is the explicit default; other palettes are opt-in."],
    ["Re-render current flame", "Applies the settings to the current viewport through a cancellable re-render."],
    ["Re-render rated flames", "Batch-replaces rated PNGs in place while preserving star folders and source .flame files."],
]
story.append(table(image_rows, [49 * mm, W - 49 * mm], compact=True))
story.append(Spacer(1, 7))
story.append(callout("Image settings affect the current viewport only after you explicitly re-render. The source .flame pair remains the durable record of the genome.", fill=ORANGE_LIGHT, border=ORANGE, style="Body"))
story.append(PageBreak())

# AI and diagnostics
story.append(P("5. AI scoring, training, and evaluation", "Section"))
story.append(P("AI is optional and independently controlled. Starting or stopping it does not start or stop the renderer.", "Lead"))
ai_rows = [
    ["Control", "What it does"],
    ["Start AI scoring", "Checks the runtime, scans existing complete rendered pairs, then watches for newly completed pairs."],
    ["Stop AI scoring", "Stops background scoring while leaving files and manual browsing available."],
    ["Train Model", "Snapshots current human ratings, trains the ordinal head, validates, stores atomically, activates, and rescales rendered candidates."],
    ["Rescore rated", "Scores rated images for comparison and updates score prefixes without changing their star folders."],
    ["AI status", "Shows model version, pending images, progress, current device, and runtime state."],
]
story.append(table(ai_rows, [39 * mm, W - 39 * mm], compact=True))
story.append(Spacer(1, 8))
story.append(P("Training behavior", "Subsection"))
story.append(bullet("The five rating folders are ordinal labels, not five unrelated image categories."))
story.append(bullet("The dataset is split into train, validation, and test groups only in Phase 2. Stable source IDs or genome families are preferred for split boundaries."))
story.append(bullet("The app reports ordinal accuracy, mean absolute rating error, Spearman or rank correlation, and calibration. It also evaluates separate control images."))
story.append(bullet("Small or imbalanced datasets can still train, but readiness colors and reliability warnings are heuristics, not scientific guarantees."))
story.append(Spacer(1, 3))
story.append(ReadinessGraphic(W))
story.append(P("Dataset Statistics shows live counts for ratings 1 through 5 as a bar graph: red means very small or missing data, amber means usable but weak, and green means reasonably balanced. The colors describe readiness, not artistic quality.", "Small"))
story.append(Spacer(1, 5))
story.append(P("Diagnostics and CUDA", "Subsection"))
story.append(P("Diagnostics reports the renderer backend, Python version, PyTorch version, CUDA availability, GPU name, active device, rating-pair status, and AI state. DINOv2 scoring and training require the CUDA/PyTorch runtime. If CUDA is unavailable, manual rendering, browsing, and rating remain operational while AI is clearly disabled or warned about.", "Body"))
story.append(callout("A successful training run replaces the active preference model, then rescans existing rendered candidates and continues with newly arriving candidates. It does not rewrite the human rating folders.", fill=TEAL_LIGHT, border=TEAL, style="Body"))
story.append(PageBreak())

# Rating and practical workflows
story.append(P("6. Rating and iteration", "Section"))
story.append(P("The viewport shows one image at a time. Alongside it, the app can show the filename and source ID, AI score, scored or pending state, active model version, and the human rating when you are viewing a rated image.", "Lead"))
rating_rows = [
    ["Control", "Use it when..."],
    ["1 to 5 stars", "You want to assign or correct the human preference label. The PNG and .flame move together."],
    ["Undo", "You made a rating mistake and want to restore the previous location."],
    ["Previous / Next", "You want normal browsing, including candidates with low or no AI scores."],
    ["Zoom to fit / Actual size", "You want to compare the full composition or inspect render detail."],
]
story.append(table(rating_rows, [38 * mm, W - 38 * mm], compact=True))
story.append(Spacer(1, 9))
story.append(P("Three useful operating patterns", "Subsection"))
story.append(cards([
    ("DISCOVERY", "Render a broad batch. Start AI scoring. Review best-first, then browse around it and rate both favorites and misses."),
    ("SEEDED TASTE", "Place examples you already understand into ratings/1 through ratings/5. Train, render new candidates, and correct the model as needed."),
    ("ITERATION", "After a new round of ratings, Train Model again. Compare the new ranking, inspect metrics, and keep only the labels you personally trust."),
], W))
story.append(Spacer(1, 12))
story.append(P("Troubleshooting", "Subsection"))
trouble_rows = [
    ["Situation", "What it means / what to do"],
    ["AI scoring is disabled", "Open Diagnostics. Confirm the CUDA/PyTorch runtime, GPU, and active device. Manual workflow is intentionally still available."],
    ["A candidate is pending", "It may not have a model score yet, or its PNG/.flame pair is incomplete. Do not score a partially written file."],
    ["A candidate has a low score", "It remains in rendered/ and remains browsable. The score only changes ordering, never availability."],
    ["Metrics say unreliable", "The corpus or split is too small for a meaningful held-out result. Continue rating and treat the model as exploratory."],
    ["You want to see everything", "Stop AI scoring or use Previous and Next. AI-first display never removes normal browsing."],
]
story.append(table(trouble_rows, [43 * mm, W - 43 * mm], compact=True))
story.append(Spacer(1, 10))
story.append(callout("Best practice: keep the ratings folders human-controlled, keep source IDs stable, use explicit re-render actions, and treat every AI suggestion as a shortcut to inspect - never as a verdict.", fill=GREEN_LIGHT, border=GREEN, style="Lead"))
story.append(Spacer(1, 12))
story.append(P("In one sentence", "Subsection"))
story.append(P("Native Fractal-Flame Curator turns random or imported fractal possibilities into a calm, repeatable loop where your ratings teach the system what to show first, while you remain in control of what counts.", "Quote"))

OUT.parent.mkdir(parents=True, exist_ok=True)
doc.build(story)
print(OUT)
