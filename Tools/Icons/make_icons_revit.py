# Icone della ribbon in stile Revit (voce 226): disegno a linee sottili allineate ai pixel, riempimenti chiari piatti,
# un solo colore d'accento blu e l'arancione solo per i punti della nuvola. Nessuna sfumatura. Contenuto entro 2..30 px
# (margine come le icone di Revit, che non vanno a filo del bordo). Stessi nomi di make_icons.py.
#   python3 make_icons_revit.py [--install]
import os, subprocess, sys, random, math

HERE = os.path.dirname(os.path.abspath(__file__))
SVG = os.path.join(HERE, "svg"); PNG = os.path.join(HERE, "png")
RES = os.path.join(HERE, "..", "..", "Resources")

K = "#4D4D4D"        # linee
F = "#FFFFFF"        # riempimento chiaro
G = "#E3E3E3"        # riempimento grigio
G2 = "#C8C8C8"       # faccia in ombra
B = "#2E75B6"        # accento blu (Revit)
BL = "#BDD7EE"       # blu chiaro
O = "#E8833A"        # punti della nuvola
R_, G_, V_ = "#D9534F", "#5CB85C", "#2E75B6"   # assi
A = f'stroke="{K}" stroke-width="1" stroke-linejoin="round" stroke-linecap="round"'

def svg(body):
    return f'<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32" viewBox="0 0 32 32" shape-rendering="geometricPrecision">{body}</svg>'
def P(pts): return " ".join(f"{x},{y}" for x, y in pts)
def poly(pts, fill=F, extra=""): return f'<polygon points="{P(pts)}" fill="{fill}" {A} {extra}/>'
def rect(x, y, w, h, fill=F, rx=0, extra=""): return f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{rx}" fill="{fill}" {A} {extra}/>'
def ln(x1, y1, x2, y2, col=K, w=1, dash=None):
    d = f' stroke-dasharray="{dash}"' if dash else ""
    return f'<line x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}" stroke="{col}" stroke-width="{w}" stroke-linecap="round"{d}/>'
def arrow(x1, y1, x2, y2, col=K, w=1.5, head=3.5):
    a = math.atan2(y2 - y1, x2 - x1); bx, by = x2 - head * math.cos(a), y2 - head * math.sin(a)
    px, py = -math.sin(a) * head * 0.5, math.cos(a) * head * 0.5
    return ln(x1, y1, bx, by, col, w) + f'<polygon points="{x2},{y2} {bx+px:.2f},{by+py:.2f} {bx-px:.2f},{by-py:.2f}" fill="{col}"/>'
def dots(pts, r=1.1, c=O): return "".join(f'<circle cx="{x}" cy="{y}" r="{r}" fill="{c}"/>' for x, y in pts)
def scatter(keep, n, seed, c=O, r=0.9):
    rnd = random.Random(seed); out = []; t = 0
    while len(out) < n and t < 8000:
        t += 1; x = 2 + rnd.random() * 28; y = 2 + rnd.random() * 28
        if keep(x, y) and all((x - a) ** 2 + (y - b) ** 2 > 7 for a, b in out): out.append((round(x, 1), round(y, 1)))
    return dots(out, r, c)
def box(cx, cy, w, h, top=F, left=G, right=G2):
    q = w / 2.0
    return (poly([(cx - w, cy - h - q), (cx, cy - h), (cx, cy), (cx - w, cy - q)], left)
            + poly([(cx + w, cy - h - q), (cx, cy - h), (cx, cy), (cx + w, cy - q)], right)
            + poly([(cx, cy - h - 2 * q), (cx + w, cy - h - q), (cx, cy - h), (cx - w, cy - h - q)], top))
def cloud_path(cx, cy, s):
    p = lambda x, y: f"{cx + x * s:.2f} {cy + y * s:.2f}"
    return (f"M{p(-9,4)} A4.5 4.5 0 0 1 {p(-8,-3.5)} A6 6 0 0 1 {p(2,-7)} A5 5 0 0 1 {p(8.5,-2)} "
            f"A3.6 3.6 0 0 1 {p(9,4)} Z")
def cloud(cx, cy, s, fill=F):
    return f'<path d="{cloud_path(cx, cy, s)}" fill="{fill}" {A}/>'
def check(x, y): return f'<circle cx="{x}" cy="{y}" r="5" fill="{B}"/><polyline points="{x-2.5},{y} {x-0.7},{y+2} {x+2.6},{y-2}" fill="none" stroke="white" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/>'

ICONS = {}
# -------------------------------------------------------------- Nuvole e rilievo
ICONS["library"] = svg(cloud(14, 11, 1.1) + dots([(8, 11), (12, 8), (16, 6.5), (19, 9), (11, 13)], 1.0)
                       + '<path d="M13.5 17.5 h5 l2 2 h8 v9 h-15 z" fill="' + G + '" ' + A + '/>' + rect(13.5, 20.5, 15, 8, BL))
ICONS["survey"] = svg(arrow(11, 21, 11, 3, V_) + arrow(11, 21, 29, 21, R_) + arrow(11, 21, 3, 29, G_)
                      + f'<circle cx="11" cy="21" r="2" fill="{F}" {A}/>')
ICONS["measure"] = svg(poly([(3, 22), (22, 3), (29, 10), (10, 29)], F)
                       + "".join(ln(3 + k * 2.7 + 0.7, 22 - k * 2.7 + 0.7, 3 + k * 2.7 + (3.8 if k % 2 == 0 else 2.4), 22 - k * 2.7 + (3.8 if k % 2 == 0 else 2.4)) for k in range(1, 7))
                       + dots([(6, 26), (26, 6)], 1.8, B))
# -------------------------------------------------------------- Analisi scostamenti
ICONS["deviation"] = svg(box(14, 29, 11, 12) + poly([(3, 17.5), (14, 23), (14, 29), (3, 23.5)], BL)
                         + scatter(lambda x, y: 18 < x < 29 and 9 < y < 20, 7, 3) + arrow(19, 24, 28, 19.5, B, 1.4, 3))
ICONS["analyze_view"] = svg(rect(3.5, 4.5, 23, 18, F) + ln(3.5, 8.5, 26.5, 8.5)
                            + scatter(lambda x, y: 6 < x < 24 and 11 < y < 20, 9, 5) + check(25, 24))
ICONS["analyze_sel"] = svg(box(9.5, 29, 6, 10) + box(21.5, 28, 6, 16, BL, "#9DC3E6", "#6FA8DC")
                           + f'<rect x="13.5" y="4.5" width="16" height="25" fill="none" stroke="{B}" stroke-width="1" stroke-dasharray="2 1.5"/>')
ICONS["missing_zones"] = svg(rect(3.5, 5.5, 18, 20, G) + f'<rect x="8.5" y="10.5" width="9" height="9" fill="{F}" stroke="{B}" stroke-width="1" stroke-dasharray="2 1.5"/>'
                             + scatter(lambda x, y: 9 < x < 17 and 11 < y < 19, 5, 9)
                             + poly([(24, 16.5), (30.5, 28.5), (17.5, 28.5)], "#F2C14E") + ln(24, 20.5, 24, 24.5, K, 1.5) + dots([(24, 26.6)], 0.8, K))
ICONS["colormap"] = svg(rect(5.5, 3.5, 7, 25, F)
                        + "".join(f'<rect x="6" y="{4 + i * 6}" width="6" height="6" fill="{c}"/>' for i, c in enumerate(["#D9534F", "#F0AD4E", "#5CB85C", "#5B9BD5"]))
                        + "".join(ln(16, 6.5 + i * 6, 27, 6.5 + i * 6, "#9A9A9A", 1.5) for i in range(4)))
ICONS["recompute"] = svg('<path d="M24.5 10.5 A10 10 0 1 0 26 18" fill="none" stroke="' + B + '" stroke-width="2" stroke-linecap="round"/>'
                         + f'<polygon points="28,6.5 27.5,13.5 21.5,10.5" fill="{B}"/>' + dots([(13, 16), (16, 13.5), (19, 16), (16, 18.5)], 1.2))
ICONS["legend"] = svg(rect(4.5, 3.5, 23, 25, F)
                      + "".join(f'<rect x="8" y="{7 + i * 5}" width="4" height="3" fill="{c}" {A}/>' + ln(15, 8.5 + i * 5, 24, 8.5 + i * 5, "#9A9A9A", 1.5)
                                for i, c in enumerate(["#D9534F", "#F0AD4E", "#5CB85C", "#5B9BD5"])))
ICONS["tolerances"] = svg("".join(ln(4, y, 28, y, "#9A9A9A", 1.5) + f'<circle cx="{x}" cy="{y}" r="2.6" fill="{F}" {A}/>' for y, x in [(8, 21), (16, 11), (24, 17)]))
# -------------------------------------------------------------- Visualizzazione
ICONS["rgb"] = svg(box(16, 29, 12, 13, "#D9534F", "#5CB85C", "#5B9BD5"))
ICONS["normals"] = svg(box(16, 29, 10, 10) + arrow(16, 13, 16, 2.5, B) + arrow(10.5, 22.5, 3, 26, B) + arrow(21.5, 22.5, 29, 26, B))
ICONS["xray"] = svg(box(16, 29, 11, 13, "none", "none", "none") + ln(16, 16, 16, 29, K, 1, "1.5 1.5")
                    + scatter(lambda x, y: 6 < x < 26 and 9 < y < 26, 10, 13, "#8C8C8C") + box(16, 25, 5, 5, BL, "#9DC3E6", "#6FA8DC"))
ICONS["cloud_single"] = svg(cloud(11, 11, 0.75) + dots([(7, 11), (10, 8.5), (13, 9.5), (11, 12.5)], 1.0, O)
                            + cloud(21, 22, 0.75) + dots([(17, 22), (20, 19.5), (23, 20.5), (21, 23.5)], 1.0, B))
ICONS["elevation"] = svg("".join(poly([(3, y), (13, y - 4.5), (23, y), (13, y + 4.5)], c) for y, c in [(25, "#5B9BD5"), (19, "#5CB85C"), (13, "#F0AD4E"), (7, "#D9534F")])
                         + arrow(28, 28, 28, 4, K, 1.3, 3))
ICONS["appearance"] = svg(dots([(6, 8)], 1.2, O) + dots([(12.5, 8)], 2.0, O) + dots([(21, 8)], 3.0, O)
                          + ln(4, 18, 28, 18, "#9A9A9A", 1.5) + f'<circle cx="17" cy="18" r="2.6" fill="{F}" {A}/>'
                          + f'<circle cx="9" cy="26" r="3.5" fill="{F}" {A}/><path d="M9 22.5 a3.5 3.5 0 0 1 0 7 z" fill="{K}"/>' + rect(16.5, 23.5, 11, 5, BL))
# -------------------------------------------------------------- Ritaglio e visibilita'
def handles(pts): return "".join(f'<rect x="{x-1.5}" y="{y-1.5}" width="3" height="3" fill="{F}" stroke="{B}" stroke-width="1"/>' for x, y in pts)
ICONS["fence"] = svg(scatter(lambda x, y: True, 26, 21, "#B0B0B0", 0.8)
                     + f'<polygon points="5,9 20,4.5 28,13 23,27 8,24" fill="{BL}" fill-opacity="0.5" stroke="{B}" stroke-width="1.2" stroke-linejoin="round"/>'
                     + scatter(lambda x, y: 10 < x < 22 and 10 < y < 22, 7, 22) + handles([(5, 9), (20, 4.5), (28, 13), (23, 27), (8, 24)]))
ICONS["fence_rect"] = svg(scatter(lambda x, y: True, 26, 23, "#B0B0B0", 0.8)
                          + f'<rect x="6.5" y="7.5" width="19" height="17" fill="{BL}" fill-opacity="0.5" stroke="{B}" stroke-width="1.2"/>'
                          + scatter(lambda x, y: 9 < x < 23 and 10 < y < 22, 7, 24) + handles([(6.5, 7.5), (25.5, 7.5), (25.5, 24.5), (6.5, 24.5)]))
ICONS["fence_circle"] = svg(scatter(lambda x, y: True, 26, 25, "#B0B0B0", 0.8)
                            + f'<circle cx="16" cy="16" r="10" fill="{BL}" fill-opacity="0.5" stroke="{B}" stroke-width="1.2"/>'
                            + scatter(lambda x, y: (x - 16) ** 2 + (y - 16) ** 2 < 60, 7, 26) + handles([(26, 16)]))
ICONS["clip_invert"] = svg(rect(3.5, 3.5, 25, 25, BL) + rect(10.5, 10.5, 11, 11, F)
                           + arrow(12.5, 14, 20, 14, B, 1.2, 2.6) + arrow(19.5, 18, 12, 18, B, 1.2, 2.6))
ICONS["clip_manage"] = svg(rect(9.5, 3.5, 18, 15, G) + rect(6.5, 7.5, 18, 15, G) + rect(3.5, 11.5, 18, 15, F)
                           + f'<rect x="7.5" y="15.5" width="10" height="7" fill="{BL}" stroke="{B}" stroke-width="1" stroke-dasharray="2 1.5"/>'
                           + f'<polygon points="23.5,20.5 28.5,20.5 28.5,29.5 26,27.5 23.5,29.5" fill="{B}"/>')
ICONS["onoff"] = svg(f'<path d="M2.5 16 Q16 4 29.5 16 Q16 28 2.5 16 Z" fill="{F}" {A}/>'
                     + f'<circle cx="16" cy="16" r="5.5" fill="{B}"/><circle cx="16" cy="16" r="2.3" fill="{K}"/><circle cx="17.6" cy="14.4" r="1" fill="white"/>')
ICONS["visibility"] = svg("".join(rect(3.5, y - 2.5, 9, 5, B if on else G, 2.5) + f'<circle cx="{10 if on else 6}" cy="{y}" r="1.7" fill="{F}" {A}/>'
                                  + ln(16, y, 28, y, "#9A9A9A", 1.5) for y, on in [(7, True), (16, False), (25, True)]))
# -------------------------------------------------------------- Impostazioni
def gear(cx, cy, r, teeth=8):
    pp = []
    for i in range(teeth * 2):
        a = math.pi * i / teeth; rr = r if i % 2 == 0 else r * 0.8
        for d in (-0.22, 0.22):
            pp.append(f"{cx + rr * math.cos(a + d * math.pi / teeth * 2):.2f},{cy + rr * math.sin(a + d * math.pi / teeth * 2):.2f}")
    return f'<polygon points="{" ".join(pp)}" fill="{G}" {A}/><circle cx="{cx}" cy="{cy}" r="{r*0.38}" fill="{F}" {A}/>'
ICONS["settings"] = svg(gear(16, 16, 13, 9))

def main():
    os.makedirs(SVG, exist_ok=True); os.makedirs(PNG, exist_ok=True)
    for name, s in ICONS.items():
        p = os.path.join(SVG, name + ".svg")
        open(p, "w", encoding="utf-8").write(s)
        for size in (16, 32, 96):
            subprocess.run(["rsvg-convert", "-w", str(size), "-h", str(size), "-o", os.path.join(PNG, f"{name}{size}.png"), p], check=True)
    if "--install" in sys.argv:
        import shutil
        for name in ICONS:
            for size in (16, 32):
                shutil.copy(os.path.join(PNG, f"{name}{size}.png"), os.path.join(RES, f"{name}{size}.png"))
    print(len(ICONS), "icone")

if __name__ == "__main__":
    main()
