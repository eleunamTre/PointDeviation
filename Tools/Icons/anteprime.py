# Anteprime per l'approvazione: tavola delle icone (96, 32 e 16 px separati) e ribbon riorganizzata.
from PIL import Image, ImageDraw, ImageFont
import importlib.util
spec = importlib.util.spec_from_file_location("m", "make_icons.py"); m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
F = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"; FB = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
f = ImageFont.truetype(F, 13); fb = ImageFont.truetype(FB, 16); fs = ImageFont.truetype(F, 10)
cell = 150; W = cell * 8 + 40; H = 40 + len(m.ORDER) * (cell + 80)
img = Image.new("RGB", (W, H), "#F5F6F8"); d = ImageDraw.Draw(img); y = 20
for title, names in m.ORDER:
    d.text((20, y), title, fill="#33404F", font=fb); y += 28
    for i, n in enumerate(names):
        x = 20 + i * cell
        d.rounded_rectangle([x, y, x + cell - 12, y + cell + 18], 8, fill="white", outline="#D5DBE3")
        big = Image.open(f"png/{n}96.png"); img.paste(big, (x + (cell - 12 - 96) // 2, y + 6), big)
        d.line([x + 8, y + 108, x + cell - 20, y + 108], fill="#E3E7EC")
        s32 = Image.open(f"png/{n}32.png"); img.paste(s32, (x + 22, y + 114), s32)
        s16 = Image.open(f"png/{n}16.png"); img.paste(s16, (x + 80, y + 122), s16)
        d.text((x + 24, y + 148), "32", fill="#8A97A8", font=fs); d.text((x + 82, y + 148), "16", fill="#8A97A8", font=fs)
        d.text((x + 4, y + cell + 22), n, fill="#5B6B80", font=f)
    y += cell + 52
img.save("tavola_icone.png")

f = ImageFont.truetype(F, 11)
panels = [("Nuvole e rilievo", [("L", "library", "Libreria\nnuvole"), ("L", "survey", "Coordinate\ndi rilievo"), ("L", "measure", "Misura\npunti")]),
 ("Analisi scostamenti", [("L", "deviation", "Mostra\nanalisi", True), ("L", "analyze_view", "Analizza\nvista"), ("L", "analyze_sel", "Analizza\nselezione"), ("L", "missing_zones", "Zone\nmancanti"), ("L", "colormap", "Profili\ncolore"), ("L", "recompute", "Aggiorna"), ("S", [("legend", "Legenda"), ("tolerances", "Impostazioni")])]),
 ("Visualizzazione", [("L", "rgb", "Modalità\ncolore ▾"), ("S", [(None, "Qualità automatica ▾"), ("appearance", "Aspetto")])]),
 ("Ritaglio e visibilità", [("L", "fence", "Recinto\n▾"), ("L", "clip_manage", "Ritagli\nsalvati"), ("L", "onoff", "Mostra\nnuvole"), ("L", "visibility", "Gestisci\nnuvole"), ("S", [("clip_invert", "Inverti ritaglio")])]),
 ("Impostazioni", [("L", "settings", "Avanzate")])]
wid = lambda it: 64 if it[0] == "L" else 150
W = sum(sum(wid(i) for i in p[1]) + 16 for p in panels) + 10; H = 118
img = Image.new("RGB", (W, H + 26), "#FFFFFF"); d = ImageDraw.Draw(img)
d.rectangle([0, 0, W, 22], fill="#F0F0F0"); d.text((8, 5), "Point Deviation", fill="#1E1E1E", font=f)
x = 4; top = 26
for title, items in panels:
    pw = sum(wid(i) for i in items) + 12
    d.rectangle([x, top, x + pw, top + H - 2], fill="#F7F7F7"); d.line([x + pw, top + 4, x + pw, top + H - 6], fill="#D0D0D0")
    tw = d.textlength(title, font=f); d.text((x + pw / 2 - tw / 2, top + H - 18), title, fill="#444", font=f)
    cx = x + 6
    for it in items:
        if it[0] == "L":
            if len(it) > 3: d.rounded_rectangle([cx + 1, top + 3, cx + 61, top + 82], 3, fill="#DCEBFA", outline="#7FB0E6")
            ic = Image.open(f"png/{it[1]}32.png"); img.paste(ic, (cx + 16, top + 8), ic)
            for k, ln in enumerate(it[2].split("\n")):
                tw = d.textlength(ln, font=f); d.text((cx + 32 - tw / 2, top + 46 + k * 14), ln, fill="#222", font=f)
            cx += 64
        else:
            for k, (ic, lab) in enumerate(it[1]):
                yy = top + 8 + k * 24
                if ic: im = Image.open(f"png/{ic}16.png"); img.paste(im, (cx + 4, yy + 2), im); d.text((cx + 24, yy + 3), lab, fill="#222", font=f)
                else: d.rectangle([cx + 4, yy, cx + 140, yy + 19], fill="white", outline="#ABABAB"); d.text((cx + 8, yy + 3), lab, fill="#222", font=f)
            cx += 150
    x += pw + 4
img.resize((img.width * 2, img.height * 2), Image.LANCZOS).save("anteprima_ribbon.png")
