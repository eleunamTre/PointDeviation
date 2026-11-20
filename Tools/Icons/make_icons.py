# Icone della ribbon (stile isometrico a due colori: blu cobalto e arancione corallo). Genera gli SVG in Tools/Icons/svg e i PNG 16/32 in Tools/Icons/png;
# con --install li copia in Resources (nomi <icona>16.png / <icona>32.png). Serve rsvg-convert.
#   python3 make_icons.py [--install]
import os, subprocess, sys, random

HERE = os.path.dirname(os.path.abspath(__file__))
SVG = os.path.join(HERE, "svg"); PNG = os.path.join(HERE, "png")
RES = os.path.join(HERE, "..", "..", "Resources")

# voce 225: stile tecnico/BIM. Contorni sottili e scuri, solidi in grigio acciaio con facce ombreggiate, sfumatura
# azzurro -> arancione dove si mostra l'analisi, micro-puntini sparsi arancioni e azzurri come una scansione laser.
INK = "#1E293B"; SW = 1.2
ST_T, ST_L, ST_R = "#E2E8F0", "#CBD5E1", "#94A3B8"     # facce dei solidi: sopra, sinistra, destra (piu' in ombra)
AZ, AR = "#3B82F6", "#F97316"                           # azzurro e arancione
PAPER = "#F8FAFC"

def defs():
    return ('<defs>'
            f'<linearGradient id="dev" x1="0" y1="1" x2="1" y2="0"><stop offset="0" stop-color="{AZ}"/>'
            f'<stop offset="0.5" stop-color="#E9D5C3"/><stop offset="1" stop-color="{AR}"/></linearGradient>'
            f'<linearGradient id="scale" x1="0" y1="1" x2="0" y2="0"><stop offset="0" stop-color="{AZ}"/>'
            f'<stop offset="0.5" stop-color="#EDE4DA"/><stop offset="1" stop-color="{AR}"/></linearGradient>'
            f'<linearGradient id="steelL" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#D5DDE7"/><stop offset="1" stop-color="{ST_L}"/></linearGradient>'
            f'<linearGradient id="steelR" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#A9B6C7"/><stop offset="1" stop-color="{ST_R}"/></linearGradient>'
            f'<linearGradient id="therm" x1="0" y1="1" x2="0" y2="0"><stop offset="0" stop-color="#3B82F6"/><stop offset="0.38" stop-color="#10B981"/>'
            f'<stop offset="0.68" stop-color="#FACC15"/><stop offset="1" stop-color="#EF4444"/></linearGradient>'
            f'<linearGradient id="thermD" x1="0" y1="1" x2="1" y2="0"><stop offset="0" stop-color="#3B82F6"/><stop offset="0.4" stop-color="#10B981"/>'
            f'<stop offset="0.7" stop-color="#FACC15"/><stop offset="1" stop-color="#EF4444"/></linearGradient>'
            f'<linearGradient id="elev" x1="0" y1="1" x2="0" y2="0"><stop offset="0" stop-color="#22C55E"/><stop offset="0.5" stop-color="#FACC15"/><stop offset="1" stop-color="#EF4444"/></linearGradient>'
            f'<linearGradient id="ochre" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#FCD34D"/><stop offset="1" stop-color="#F59E0B"/></linearGradient>'
            f'<linearGradient id="indigo" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#A5B4FC"/><stop offset="1" stop-color="#6366F1"/></linearGradient>'
            f'<linearGradient id="teal" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#5EEAD4"/><stop offset="1" stop-color="#0D9488"/></linearGradient>'
            f'<linearGradient id="orange" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#FDBA74"/><stop offset="1" stop-color="#F97316"/></linearGradient>'
            f'<linearGradient id="gear" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#94A3B8"/><stop offset="1" stop-color="#64748B"/></linearGradient>'
            '</defs>')

def svg(body):
    return f'<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32" viewBox="0 0 32 32">{defs()}{body}</svg>'

A = f'stroke="{INK}" stroke-width="{SW}" stroke-linejoin="round" stroke-linecap="round"'

def box(cx, cy, w, h, top=ST_T, left="url(#steelL)", right="url(#steelR)", extra=""):
    """Parallelepipedo isometrico: (cx,cy) = vertice in basso al centro, w = mezza larghezza, h = altezza."""
    q = w / 2.0
    t = f"{cx},{cy-h-2*q} {cx+w},{cy-h-q} {cx},{cy-h} {cx-w},{cy-h-q}"
    l = f"{cx-w},{cy-h-q} {cx},{cy-h} {cx},{cy} {cx-w},{cy-q}"
    r = f"{cx+w},{cy-h-q} {cx},{cy-h} {cx},{cy} {cx+w},{cy-q}"
    return (f'<polygon points="{l}" fill="{left}" {A} {extra}/><polygon points="{r}" fill="{right}" {A} {extra}/>'
            f'<polygon points="{t}" fill="{top}" {A} {extra}/>')

def faces(cx, cy, w, h):
    q = w / 2.0
    return dict(top=[(cx, cy-h-2*q), (cx+w, cy-h-q), (cx, cy-h), (cx-w, cy-h-q)],
                left=[(cx-w, cy-h-q), (cx, cy-h), (cx, cy), (cx-w, cy-q)],
                right=[(cx+w, cy-h-q), (cx, cy-h), (cx, cy), (cx+w, cy-q)])

def in_poly(x, y, poly):
    c = False; n = len(poly)
    for k in range(n):
        x1, y1 = poly[k]; x2, y2 = poly[(k + 1) % n]
        if (y1 > y) != (y2 > y) and x < (x2 - x1) * (y - y1) / (y2 - y1) + x1: c = not c
    return c

def scan(keep, n, seed, cols=(AR, AZ), r=(0.45, 0.75), box_=(1, 1, 31, 31), op=1.0):
    """Micro-puntini sparsi (scansione laser) dentro la zona keep(x,y)."""
    rnd = random.Random(seed); out = []; t = 0
    x0, y0, x1, y1 = box_
    while len(out) < n and t < 20000:
        t += 1
        x = x0 + rnd.random() * (x1 - x0); y = y0 + rnd.random() * (y1 - y0)
        if not keep(x, y): continue
        if any((x - a) ** 2 + (y - b) ** 2 < 1.3 for a, b, _, _ in out): continue
        out.append((x, y, cols[rnd.randrange(len(cols))], r[0] + rnd.random() * (r[1] - r[0])))
    return "".join(f'<circle cx="{x:.2f}" cy="{y:.2f}" r="{rr:.2f}" fill="{c}" opacity="{op}"/>' for x, y, c, rr in out)

def cloud_shape(cx, cy, s=1.0):
    """Sagoma di nuvola (contorno), larga circa 22*s."""
    p = lambda x, y: f"{cx + x * s:.2f} {cy + y * s:.2f}"
    return (f"M{p(-9,4)} A4 4 0 0 1 {p(-8.5,-3.5)} A5.5 5.5 0 0 1 {p(1,-7)} A4.5 4.5 0 0 1 {p(8,-3)} "
            f"A3.8 3.8 0 0 1 {p(9.5,4)} Z")

def in_cloud(cx, cy, s=1.0):
    lobes = [(-6, 0.5, 4), (0, -2, 5.5), (5.5, 0.5, 4.2), (0, 1.5, 5)]
    return lambda x, y: y < cy + 3.6 * s and any(((x - cx) / s - a) ** 2 + ((y - cy) / s - b) ** 2 <= rr * rr for a, b, rr in lobes)

def line(x1, y1, x2, y2, col=INK, w=SW, dash=None):
    d = f' stroke-dasharray="{dash}"' if dash else ""
    return f'<line x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}" stroke="{col}" stroke-width="{w}" stroke-linecap="round"{d}/>'

def arrow(x1, y1, x2, y2, col=INK, w=SW, head=3.0):
    import math
    a = math.atan2(y2 - y1, x2 - x1); bx, by = x2 - head * math.cos(a), y2 - head * math.sin(a)
    px, py = -math.sin(a) * head * 0.55, math.cos(a) * head * 0.55
    return line(x1, y1, bx, by, col, w) + f'<polygon points="{x2},{y2} {bx+px:.2f},{by+py:.2f} {bx-px:.2f},{by-py:.2f}" fill="{col}"/>'

def tick(x, y, s=1.0, col="#10B981"):
    return f'<polyline points="{x-3*s},{y} {x-0.8*s},{y+2.4*s} {x+3.6*s},{y-2.8*s}" fill="none" stroke="{col}" stroke-width="{2.2*s}" stroke-linecap="round" stroke-linejoin="round"/>'

ICONS = {}
# voce 225: codice colore per pannello e una sagoma diversa per ogni icona
OCHRE, RED, GREEN, BLUE = "#F59E0B", "#EF4444", "#10B981", "#3B82F6"
INDIGO, CYAN, TEAL, EMER, ORNG, STEEL = "#6366F1", "#06B6D4", "#0D9488", "#10B981", "#F97316", "#64748B"
def pts(v): return " ".join(f"{x:.2f},{y:.2f}" for x, y in v)

# ----------------------------------------------------------------------------------------- Progetto e dati (ocra, assi RGB)
# voce 225: pannello «Nuvole e rilievo» piu' minimal: forme piene e grandi, pochi dettagli
def _blob_cloud(cx, cy, s, fill, edge=INK, sw=1.4):
    p = lambda x, y: f"{cx + x * s:.2f} {cy + y * s:.2f}"
    return (f'<path d="M{p(-10,5)} A5 5 0 0 1 {p(-9,-4)} A6.5 6.5 0 0 1 {p(2,-8)} A5.5 5.5 0 0 1 {p(9.5,-2.5)} '
            f'A4.5 4.5 0 0 1 {p(10.5,5)} Z" fill="{fill}" stroke="{edge}" stroke-width="{sw}" stroke-linejoin="round"/>')
ICONS["library"] = svg(  # nuvola piena con pochi punti e cartella davanti
    _blob_cloud(15.5, 12.5, 1.1, "#DBEAFE")
    + "".join(f'<circle cx="{x}" cy="{y}" r="1.25" fill="{c}"/>' for x, y, c in [(7, 13, BLUE), (11, 9.5, ORNG), (15, 6.5, BLUE), (19, 9.5, BLUE), (23, 8, ORNG), (9, 16.5, ORNG)])
    + f'<path d="M12 17 h6.5 l2.2 2.6 h9.3 v9.4 a1.4 1.4 0 0 1 -1.4 1.4 h-15.2 a1.4 1.4 0 0 1 -1.4 -1.4 z" fill="#D97706" stroke="{INK}" stroke-width="1.4" stroke-linejoin="round"/>'
    + f'<path d="M12 21.4 h18 v7.6 a1.4 1.4 0 0 1 -1.4 1.4 h-15.2 a1.4 1.4 0 0 1 -1.4 -1.4 z" fill="{OCHRE}" stroke="{INK}" stroke-width="1.4" stroke-linejoin="round"/>')
ICONS["survey"] = svg(  # assi spessi che riempiono l'icona
    arrow(11, 21, 11, 1.5, BLUE, 2.6, 5) + arrow(11, 21, 30.5, 21, RED, 2.6, 5) + arrow(11, 21, 1.5, 30.5, GREEN, 2.6, 5)
    + f'<circle cx="11" cy="21" r="3.6" fill="{OCHRE}" stroke="{INK}" stroke-width="1.4"/>')

# ----------------------------------------------------------------------------------------- Analisi scostamenti (termica)
ICONS["deviation"] = svg(  # parete ad L con mappa termica
    f'<polygon points="3,11 15,17 15,30 3,24" fill="url(#therm)" {A}/>'
    + f'<polygon points="15,17 29,10 29,23 15,30" fill="url(#thermD)" {A}/>'
    + f'<polygon points="3,11 6,9.5 18,15.5 15,17" fill="{ST_T}" {A}/><polygon points="15,17 18,15.5 29,10 29,10" fill="{ST_T}" {A}/>'
    + f'<polygon points="18,15.5 29,10 26,8.5 15,14 " fill="{ST_T}" {A}/>'
    + scan(lambda x, y: in_poly(x, y, [(3, 11), (15, 17), (15, 30), (3, 24)]) or in_poly(x, y, [(15, 17), (29, 10), (29, 23), (15, 30)]), 26, 51, ("#FFFFFF",), (0.35, 0.55), op=0.8))
ICONS["analyze_view"] = svg(  # finestra/schermo con griglia e spunta
    f'<rect x="3" y="4" width="24" height="19" rx="1.6" fill="#EFF6FF" {A}/>' + line(3, 8.5, 27, 8.5)
    + "".join(line(x, 8.5, x, 23, "#93C5FD", 0.9) for x in (9, 15, 21)) + "".join(line(3, y, 27, y, "#93C5FD", 0.9) for y in (13.5, 18.5))
    + f'<circle cx="24" cy="23" r="6" fill="{GREEN}" {A}/>' + tick(24, 23, 1.0, "white"))
ICONS["analyze_sel"] = svg(  # pilastro selezionato con contorno luminoso
    f'<polygon points="11,8 16,5.5 21,8 21,27 16,29.5 11,27" fill="none" stroke="{BLUE}" stroke-width="5" stroke-linejoin="round" opacity="0.28"/>'
    + f'<polygon points="11,8 16,10.5 16,29.5 11,27" fill="url(#steelL)" {A}/><polygon points="16,10.5 21,8 21,27 16,29.5" fill="url(#steelR)" {A}/>'
    + f'<polygon points="11,8 16,5.5 21,8 16,10.5" fill="{ST_T}" {A}/>'
    + f'<polygon points="11,8 16,5.5 21,8 21,27 16,29.5 11,27" fill="none" stroke="{BLUE}" stroke-width="1.4" stroke-linejoin="round"/>')
ICONS["missing_zones"] = svg(  # muro con area mancante tratteggiata e avviso
    f'<rect x="3" y="5" width="22" height="20" fill="{ST_L}" {A}/>'
    + "".join(line(3, y, 25, y, "#94A3B8", 0.8) for y in (10, 15, 20)) + "".join(line(x, y0, x, y0 + 5, "#94A3B8", 0.8) for y0, xs in [(5, (9, 17)), (10, (13, 21)), (15, (9, 17)), (20, (13, 21))] for x in xs)
    + f'<rect x="9" y="9" width="10" height="9" fill="white" stroke="{RED}" stroke-width="1.2" stroke-dasharray="1.8 1.3"/>'
    + "".join(line(9 + k * 2.5, 18, 9 + k * 2.5 + 4, 9, "#FCA5A5", 0.8) for k in range(0, 3))
    + f'<polygon points="24,17 30.5,29 17.5,29" fill="{ORNG}" {A}/>' + line(24, 21, 24, 25, "white", 1.8)
    + '<circle cx="24" cy="27.2" r="0.95" fill="white"/>')
ICONS["colormap"] = svg(
    f'<rect x="11" y="2.5" width="10" height="27" rx="2" fill="url(#therm)" {A}/>'
    + "".join(line(21, y, 24.5, y, INK, 1.1) for y in (5.5, 12, 18.5, 25)))
ICONS["recompute"] = svg(  # due grandi frecce circolari
    f'<path d="M6.2 13.5 A10 10 0 0 1 24.5 9" fill="none" stroke="{BLUE}" stroke-width="3" stroke-linecap="round"/>'
    + f'<polygon points="27.6,6 27.4,14 20.6,10.2" fill="{BLUE}"/>'
    + f'<path d="M25.8 18.5 A10 10 0 0 1 7.5 23" fill="none" stroke="{RED}" stroke-width="3" stroke-linecap="round"/>'
    + f'<polygon points="4.4,26 4.6,18 11.4,21.8" fill="{RED}"/>')
ICONS["legend"] = svg(
    f'<rect x="4.5" y="3.5" width="23" height="25" rx="1.5" fill="white" {A}/>'
    + "".join(f'<rect x="8" y="{y}" width="5" height="4" rx="0.8" fill="{c}" stroke="{INK}" stroke-width="0.6"/>' + line(16, y + 2, 24, y + 2, "#64748B", 1.2)
              for y, c in [(6.5, RED), (11.5, "#FACC15"), (16.5, GREEN), (21.5, BLUE)]))
ICONS["tolerances"] = svg(
    "".join(line(5, y, 27, y, "#CBD5E1", 2) + line(5, y, x, y, c, 2) + f'<circle cx="{x}" cy="{y}" r="2.8" fill="white" stroke="{c}" stroke-width="1.8"/>'
            for y, x, c in [(8, 21, RED), (16, 12, GREEN), (24, 17, BLUE)]))

# ----------------------------------------------------------------------------------------- Visualizzazione (indaco/ciano)
_c = faces(16, 30, 12, 14)
ICONS["rgb"] = svg(  # cubo con le tre facce R, G, B
    f'<polygon points="{pts(_c["left"])}" fill="#22C55E" {A}/><polygon points="{pts(_c["right"])}" fill="{BLUE}" {A}/>'
    + f'<polygon points="{pts(_c["top"])}" fill="{RED}" {A}/>'
    + f'<polygon points="{pts(_c["top"])}" fill="white" opacity="0.15"/>')
ICONS["normals"] = svg(  # superficie ondulata con normali
    f'<path d="M2 24 Q9 17 16 22 T30 20 L30 27 Q23 29 16 29 T2 30 Z" fill="url(#indigo)" {A}/>'
    + arrow(7.5, 21.5, 5, 9, CYAN, 1.6, 3) + arrow(16, 22, 16, 6.5, CYAN, 1.6, 3) + arrow(24.5, 20.5, 27.5, 8, CYAN, 1.6, 3))
ICONS["xray"] = svg(  # sagoma di edificio trasparente con punti interni
    f'<path d="M4 29 V13 L16 4 L28 13 V29 Z" fill="{INDIGO}" fill-opacity="0.15" stroke="{INDIGO}" stroke-width="1.4" stroke-linejoin="round"/>'
    + line(4, 20, 28, 20, INDIGO, 0.9, "1.6 1.3") + line(16, 4, 16, 29, INDIGO, 0.9, "1.6 1.3")
    + scan(lambda x, y: in_poly(x, y, [(4, 29), (4, 13), (16, 4), (28, 13), (28, 29)]), 34, 61, (CYAN, INDIGO), (0.45, 0.75)))
ICONS["cloud_single"] = svg(
    f'<path d="{cloud_shape(11, 11.5, 0.75)}" fill="#EEF2FF" stroke="{INDIGO}" stroke-width="1.2"/>' + scan(in_cloud(11, 11.5, 0.75), 16, 62, (INDIGO,), (0.5, 0.8))
    + f'<path d="{cloud_shape(21, 22.5, 0.75)}" fill="#ECFEFF" stroke="{CYAN}" stroke-width="1.2"/>' + scan(in_cloud(21, 22.5, 0.75), 16, 63, (CYAN,), (0.5, 0.8)))
ICONS["elevation"] = svg(  # rilievo a gradoni con scala altimetrica
    f'<path d="M2 29 L2 23 H7 V18 H12 V12 H17 V6 H20 V12 H24 V20 H29 V29 Z" fill="url(#elev)" {A}/>'
    + line(2, 23, 29, 23, "white", 0.7, "1.4 1.2") + line(2, 18, 29, 18, "white", 0.7, "1.4 1.2") + line(2, 12, 29, 12, "white", 0.7, "1.4 1.2"))
ICONS["appearance"] = svg(  # grandezza dei punti e luminosita'
    f'<circle cx="6" cy="9" r="1.6" fill="{INDIGO}"/><circle cx="13" cy="9" r="2.6" fill="{INDIGO}"/><circle cx="22.5" cy="9" r="4" fill="{INDIGO}"/>'
    + line(4, 21, 28, 21, "#C7D2FE", 2.2) + line(4, 21, 17, 21, CYAN, 2.2) + f'<circle cx="17" cy="21" r="3.2" fill="white" stroke="{CYAN}" stroke-width="2"/>'
    + f'<circle cx="7" cy="27.5" r="0" fill="none"/>')

# ----------------------------------------------------------------------------------------- Ritaglio e visibilita' (verde/teal)
def handles(v):
    return "".join(f'<rect x="{x-1.8}" y="{y-1.8}" width="3.6" height="3.6" fill="white" stroke="{TEAL}" stroke-width="1.2"/>' for x, y in v)
def dots_bg(keep, seed, n=34):
    return scan(keep, n, seed, ("#94A3B8",), (0.4, 0.6))
ICONS["fence"] = svg(
    dots_bg(lambda x, y: True, 71)
    + f'<polygon points="5,9 20,4 28,13 23,27 8,24" fill="{EMER}" fill-opacity="0.22" stroke="{TEAL}" stroke-width="1.6" stroke-linejoin="round"/>'
    + scan(lambda x, y: in_poly(x, y, [(5, 9), (20, 4), (28, 13), (23, 27), (8, 24)]), 22, 72, (TEAL,), (0.5, 0.75))
    + handles([(5, 9), (20, 4), (28, 13), (23, 27), (8, 24)]))
ICONS["fence_rect"] = svg(
    dots_bg(lambda x, y: True, 73)
    + f'<rect x="6" y="7" width="20" height="18" fill="{EMER}" fill-opacity="0.22" stroke="{TEAL}" stroke-width="1.6"/>'
    + scan(lambda x, y: 6.5 < x < 25.5 and 7.5 < y < 24.5, 22, 74, (TEAL,), (0.5, 0.75)) + handles([(6, 7), (26, 7), (26, 25), (6, 25)]))
ICONS["fence_circle"] = svg(
    dots_bg(lambda x, y: True, 75)
    + f'<circle cx="16" cy="16" r="10.5" fill="{EMER}" fill-opacity="0.22" stroke="{TEAL}" stroke-width="1.6"/>'
    + scan(lambda x, y: (x - 16) ** 2 + (y - 16) ** 2 < 100, 22, 76, (TEAL,), (0.5, 0.75)) + line(16, 16, 26.5, 16, TEAL, 1.1)
    + f'<circle cx="16" cy="16" r="1.4" fill="{TEAL}"/>' + handles([(26.5, 16)]))
ICONS["clip_invert"] = svg(  # fuori pieno, dentro vuoto, frecce di scambio
    f'<rect x="3" y="3" width="26" height="26" rx="2" fill="url(#teal)" {A}/>'
    + f'<rect x="10" y="10" width="12" height="12" fill="white" stroke="{INK}" stroke-width="1.2" stroke-dasharray="1.8 1.3"/>'
    + arrow(12.5, 14, 20, 14, TEAL, 1.5, 2.6) + arrow(19.5, 18, 12, 18, TEAL, 1.5, 2.6))
ICONS["clip_manage"] = svg(  # pila di ritagli salvati
    f'<rect x="9" y="3" width="19" height="16" rx="1.5" fill="#CCFBF1" {A}/>'
    + f'<rect x="6" y="7" width="19" height="16" rx="1.5" fill="#99F6E4" {A}/>'
    + f'<rect x="3" y="11" width="19" height="16" rx="1.5" fill="white" {A}/>'
    + f'<rect x="6.5" y="14.5" width="12" height="9" fill="{EMER}" fill-opacity="0.25" stroke="{TEAL}" stroke-width="1.2" stroke-dasharray="1.6 1.2"/>'
    + f'<polygon points="23,20 29,20 29,30 26,27.5 23,30" fill="{TEAL}" {A}/>')
ICONS["onoff"] = svg(  # grande occhio aperto
    f'<path d="M1.5 16 Q16 3 30.5 16 Q16 29 1.5 16 Z" fill="white" stroke="{INK}" stroke-width="1.4" stroke-linejoin="round"/>'
    + f'<circle cx="16" cy="16" r="7" fill="url(#teal)" {A}/><circle cx="16" cy="16" r="3" fill="{INK}"/><circle cx="18" cy="13.8" r="1.4" fill="white"/>')
ICONS["visibility"] = svg(  # elenco di nuvole con interruttori
    "".join(f'<rect x="3" y="{y-2.6}" width="9" height="5.2" rx="2.6" fill="{on and TEAL or "#CBD5E1"}" stroke="{INK}" stroke-width="1"/>'
            + f'<circle cx="{9.4 if on else 5.6}" cy="{y}" r="1.9" fill="white" stroke="{INK}" stroke-width="0.8"/>' + line(15, y, 29, y, "#64748B", 1.6)
            for y, on in [(7, True), (16, False), (25, True)]))

# ----------------------------------------------------------------------------------------- Misure e impostazioni
ICONS["measure"] = svg(  # righello grande e pieno
    f'<polygon points="1.5,22.5 22.5,1.5 30.5,9.5 9.5,30.5" fill="{ORNG}" stroke="{INK}" stroke-width="1.4" stroke-linejoin="round"/>'
    + "".join(line(1.5 + k * 3 + 0, 22.5 - k * 3 + 0, 1.5 + k * 3 + (4.2 if k % 2 == 0 else 2.6), 22.5 - k * 3 + (4.2 if k % 2 == 0 else 2.6), INK, 1.6) for k in range(1, 7)))

def gear(cx, cy, r, teeth=8):
    import math
    pp = []
    for i in range(teeth * 2):
        a = math.pi * i / teeth
        rr = r if i % 2 == 0 else r * 0.8
        for d in (-0.22, 0.22):
            pp.append(f"{cx + rr * math.cos(a + d * math.pi / teeth * 2):.2f},{cy + rr * math.sin(a + d * math.pi / teeth * 2):.2f}")
    return (f'<polygon points="{" ".join(pp)}" fill="url(#gear)" {A}/>'
            f'<circle cx="{cx}" cy="{cy}" r="{r*0.4}" fill="white" {A}/>')
ICONS["settings"] = svg(gear(16, 16, 13.5, 9))


# ----------------------------------------------------------------------------------------- Analisi scostamenti: stile
# della prima proposta (isometrico colorato), richiesto dall'utente per questo pannello
_V1B, _V1BD, _V1O, _V1G, _V1R, _V1Y = "#2F7BD6", "#1D5AA6", "#F28C28", "#3BA55C", "#E04848", "#F2C230"
_V1E = "#5B6B80"
_V1SCALE = [_V1B, _V1G, _V1Y, _V1R]
def _v1svg(body):
    return ('<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32" viewBox="0 0 32 32"><defs>'
            f'<linearGradient id="v1scale" x1="0" y1="1" x2="0" y2="0"><stop offset="0" stop-color="{_V1B}"/><stop offset="0.35" stop-color="{_V1G}"/>'
            f'<stop offset="0.65" stop-color="{_V1Y}"/><stop offset="1" stop-color="{_V1R}"/></linearGradient></defs>{body}</svg>')
def _v1box(cx, cy, w, h, top="#EAF1FA", left="#C5D5E8", right="#A3B8D2", edge=_V1E, sw=0.8):
    q = w / 2.0
    t = f"{cx},{cy-h-2*q} {cx+w},{cy-h-q} {cx},{cy-h} {cx-w},{cy-h-q}"
    l = f"{cx-w},{cy-h-q} {cx},{cy-h} {cx},{cy} {cx-w},{cy-q}"
    r = f"{cx+w},{cy-h-q} {cx},{cy-h} {cx},{cy} {cx+w},{cy-q}"
    a = f'stroke="{edge}" stroke-width="{sw}" stroke-linejoin="round"'
    return f'<polygon points="{l}" fill="{left}" {a}/><polygon points="{r}" fill="{right}" {a}/><polygon points="{t}" fill="{top}" {a}/>'
def _v1cloud(cx, cy, rx, ry, n, seed, cols, r=1.15):
    rnd = random.Random(seed); out = []
    lobes = [(cx - rx * 0.45, cy + ry * 0.15, rx * 0.55, ry * 0.75), (cx + rx * 0.1, cy - ry * 0.25, rx * 0.6, ry * 0.85),
             (cx + rx * 0.55, cy + ry * 0.2, rx * 0.5, ry * 0.65)]
    t = 0
    while len(out) < n and t < 5000:
        t += 1
        lx, ly, ax, ay = lobes[rnd.randrange(3)]
        x = lx + (rnd.random() * 2 - 1) * ax; y = ly + (rnd.random() * 2 - 1) * ay
        if ((x - lx) / ax) ** 2 + ((y - ly) / ay) ** 2 > 1 or y > cy + ry * 0.75: continue
        if any((x - a) ** 2 + (y - b) ** 2 < (2 * r + 0.25) ** 2 for a, b, _ in out): continue
        out.append((x, y, cols[rnd.randrange(len(cols))]))
    return "".join(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="{r}" fill="{c}"/>' for x, y, c in out)
def _v1line(x1, y1, x2, y2, col=_V1E, w=1.4, dash=None):
    d = f' stroke-dasharray="{dash}"' if dash else ""
    return f'<line x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}" stroke="{col}" stroke-width="{w}" stroke-linecap="round"{d}/>'
def _v1check(x, y):
    return (f'<circle cx="{x}" cy="{y}" r="5" fill="{_V1G}" stroke="white" stroke-width="1"/>'
            f'<polyline points="{x-2.4},{y} {x-0.6},{y+1.9} {x+2.6},{y-1.9}" fill="none" stroke="white" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/>')

ICONS["deviation"] = _v1svg(
    _v1box(15, 30, 11, 13) + '<polygon points="4,17 15,22.5 15,30 4,24.5" fill="url(#v1scale)" opacity="0.92"/>'
    + _v1cloud(22, 17, 4.6, 6.5, 9, 7, _V1SCALE, 1.0))
ICONS["analyze_view"] = _v1svg(_v1box(13, 30, 10, 11) + _v1cloud(13, 13.5, 8, 4, 12, 11, _V1SCALE, 1.0) + _v1check(25, 24))
ICONS["analyze_sel"] = _v1svg(
    _v1box(10, 30, 7, 9, "#EEF2F7", "#C9D3DF", "#A7B4C4") + _v1box(22, 28, 7, 13, "#DCEBFF", "#9CC3F2", "#6FA4E6", _V1BD, 1.1)
    + f'<rect x="14" y="5" width="16" height="25" rx="1.5" fill="none" stroke="{_V1B}" stroke-width="1.2" stroke-dasharray="2.2 1.6"/>'
    + _v1cloud(22, 9, 6, 3.2, 8, 5, _V1SCALE, 0.95))
ICONS["missing_zones"] = _v1svg(
    _v1box(10, 30, 8, 10) + '<rect x="15.5" y="6" width="14" height="18" rx="2" fill="#F3E9FF" stroke="#8E5BD9" stroke-width="1.2" stroke-dasharray="2.4 1.6"/>'
    + _v1cloud(22.5, 15, 6, 6, 13, 21, ["#8E5BD9", "#B48BEA", "#6C3BC2"], 1.0)
    + f'<circle cx="26.5" cy="26" r="4.2" fill="{_V1O}" stroke="white" stroke-width="1"/>'
    + '<text x="26.5" y="29" text-anchor="middle" font-family="Segoe UI, Arial" font-weight="bold" font-size="7.5" fill="white">?</text>')
ICONS["colormap"] = _v1svg(
    f'<rect x="5" y="3" width="9" height="26" rx="2" fill="url(#v1scale)" stroke="{_V1E}" stroke-width="0.9"/>'
    + "".join(f'<rect x="17" y="{y}" width="11" height="3.4" rx="1.2" fill="{c}"/>' for y, c in [(5, _V1R), (11, _V1Y), (17, _V1G), (23, _V1B)]))
ICONS["recompute"] = _v1svg(
    f'<path d="M25.5 12 A10 10 0 1 0 26 19" fill="none" stroke="{_V1B}" stroke-width="2.4" stroke-linecap="round"/>'
    + f'<polygon points="29,8 27.8,15.4 21,12.4" fill="{_V1B}"/>' + _v1cloud(16, 16, 5.5, 3.6, 8, 9, _V1SCALE, 1.0))
ICONS["legend"] = _v1svg(
    f'<rect x="3.5" y="4" width="25" height="24" rx="2.5" fill="white" stroke="{_V1E}" stroke-width="1"/>'
    + "".join(f'<rect x="7" y="{y}" width="5" height="3.6" rx="0.8" fill="{c}"/>' + _v1line(15, y + 1.8, 25, y + 1.8, "#9AA8B8", 1.6)
              for y, c in [(7.5, _V1R), (12.5, _V1Y), (17.5, _V1G), (22.5, _V1B)]))
ICONS["tolerances"] = _v1svg(
    "".join(_v1line(5, y, 27, y, "#9AA8B8", 2) + _v1line(5, y, x, y, c, 2) + f'<circle cx="{x}" cy="{y}" r="3.2" fill="white" stroke="{c}" stroke-width="2"/>'
            for y, x, c in [(8, 20, _V1R), (16, 11, _V1G), (24, 17, _V1B)]))

ORDER = [("Nuvole e rilievo", ["library", "survey", "measure"]),
         ("Analisi scostamenti", ["deviation", "analyze_view", "analyze_sel", "missing_zones", "colormap", "recompute", "legend", "tolerances"]),
         ("Visualizzazione", ["rgb", "normals", "xray", "cloud_single", "elevation", "appearance"]),
         ("Ritaglio e visibilita'", ["fence", "fence_rect", "fence_circle", "clip_invert", "clip_manage", "onoff", "visibility"]),
         ("Impostazioni", ["settings"])]

def main():
    os.makedirs(SVG, exist_ok=True); os.makedirs(PNG, exist_ok=True)
    for name, s in ICONS.items():
        p = os.path.join(SVG, name + ".svg")
        open(p, "w", encoding="utf-8").write(s)
        # voce 225: il disegno si ritaglia sul contenuto e si ingrandisce fino a riempire l'icona (margine di 1 px):
        # nella ribbon le icone risultavano piccole, con spazi vuoti attorno
        big = os.path.join(PNG, f"{name}_512.png")
        subprocess.run(["rsvg-convert", "-w", "512", "-h", "512", "-o", big, p], check=True)
        from PIL import Image
        im = Image.open(big).convert("RGBA"); bb = im.getchannel("A").point(lambda v: 255 if v > 8 else 0).getbbox()
        if bb: im = im.crop(bb)
        os.remove(big)
        for size in (16, 32, 96):
            inner = size - (2 if size <= 32 else 6)
            k = inner / max(im.width, im.height)
            sm = im.resize((max(1, round(im.width * k)), max(1, round(im.height * k))), Image.LANCZOS)
            out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
            out.paste(sm, ((size - sm.width) // 2, (size - sm.height) // 2), sm)
            out.save(os.path.join(PNG, f"{name}{size}.png"))
    if "--install" in sys.argv:
        import shutil
        for name in ICONS:
            for size in (16, 32):
                shutil.copy(os.path.join(PNG, f"{name}{size}.png"), os.path.join(RES, f"{name}{size}.png"))
    print(len(ICONS), "icone")

if __name__ == "__main__":
    main()
