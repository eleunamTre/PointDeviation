#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Riepilogo del diario dei fotogrammi di PointDeviation (PointDeviation-frames.log, voce 234).

Uso:
    python diario.py PointDeviation-frames.log                 # ultima sessione
    python diario.py PointDeviation-frames.log --sessione -2   # penultima
    python diario.py diario.log --da 15:28:10 --per 15         # 15 s a partire da un'ora (come il nome del video)
    python diario.py diario.log --linea                        # una riga per immagine in pianta/sezione

Righe del diario: ms;tipo;campi. La sessione comincia con «E;inizio diario;modifica=N;data ora»: l'ora serve a
convertire i ms in ora del giorno (precisione di un secondo, come il nome dei video).
"""
import argparse
import collections
import re
import sys
from datetime import datetime, timedelta


def sessioni(righe):
    inizi = [i for i, r in enumerate(righe) if ';E;inizio diario' in r]
    out = []
    for k, i in enumerate(inizi):
        j = inizi[k + 1] if k + 1 < len(inizi) else len(righe)
        out.append((i, j))
    return out


def campi(r):
    p = r.rstrip('\n').split(';')
    try:
        t = float(p[0])
    except ValueError:
        return None, None, {}, p
    d = {}
    for x in p[2:]:
        if '=' in x:
            a, b = x.split('=', 1)
            d[a] = b
    return t, p[1], d, p


def fascia(n):
    n = int(n)
    if n <= 0:
        return '0'
    if n < 60:
        return '1-59'
    if n < 200:
        return '60-199'
    return '200+'


def main():
    ap = argparse.ArgumentParser(description='Riepilogo del diario dei fotogrammi')
    ap.add_argument('diario')
    ap.add_argument('--sessione', type=int, default=-1, help='indice della sessione (-1 = ultima)')
    ap.add_argument('--da', help='ora di inizio HH:MM:SS')
    ap.add_argument('--per', type=float, default=0, help='durata in secondi (con --da)')
    ap.add_argument('--linea', action='store_true', help='una riga per immagine in pianta/sezione (X;scena_immagine)')
    a = ap.parse_args()

    with open(a.diario, encoding='utf-8', errors='replace') as f:
        righe = f.readlines()
    ss = sessioni(righe)
    if not ss:
        print('nessuna sessione («inizio diario») nel file')
        return 1
    i0, i1 = ss[a.sessione]
    testa = righe[i0].rstrip('\n').split(';')
    t0 = float(testa[0])
    modifica = testa[3] if len(testa) > 3 else '?'
    try:
        ora0 = datetime.strptime(testa[4], '%Y-%m-%d %H:%M:%S')
    except (IndexError, ValueError):
        ora0 = None
    print('sessione %d di %d: %s, iniziata %s' % (len(ss) + a.sessione + 1 if a.sessione < 0 else a.sessione + 1, len(ss), modifica, testa[4] if len(testa) > 4 else '?'))

    lo, hi = -1e18, 1e18
    if a.da and ora0:
        h = datetime.strptime(ora0.strftime('%Y-%m-%d ') + a.da, '%Y-%m-%d %H:%M:%S')
        lo = t0 + (h - ora0).total_seconds() * 1000
        hi = lo + (a.per * 1000 if a.per > 0 else 1e18)
        print('intervallo: dalle %s per %s s (ms %.0f - %.0f)' % (a.da, a.per or 'fino alla fine', lo, hi if hi < 1e17 else 0))

    def ora(t):
        if not ora0:
            return '%.0f' % t
        return (ora0 + timedelta(milliseconds=t - t0)).strftime('%H:%M:%S.%f')[:-3]

    tipi = collections.Counter()
    scena = collections.Counter()
    colori = collections.defaultdict(list)
    eventi = collections.Counter()
    salti = collections.Counter()
    viste = []
    ultimo = None
    for r in righe[i0:i1]:
        t, tipo, d, p = campi(r)
        if t is None or t < lo or t > hi:
            continue
        tipi[tipo] += 1
        if tipo == 'E' and len(p) > 2:
            if p[2].startswith('vista='):
                viste.append((ora(t), p[2][6:]))
            eventi['E;' + p[2].split('=')[0]] += 1
        if tipo == 'X' and len(p) > 2:
            nome = p[2].split('=')[0]
            if nome != 'scena_immagine':
                eventi['X;' + nome + ('=' + p[2].split('=')[1] if nome in ('ricerca_matrice', 'buffer_cambiato', 'controllo_matrice') else '')] += 1
        if tipo == 'M' and 'salto' in d:
            salti[d['salto']] += 1
        if tipo == 'X' and len(p) > 2 and p[2] == 'scena_immagine':
            k = (d.get('nuvola', '?'), fascia(d.get('disegni_scena', 0)), 'faro' if int(d.get('faro_dopo_disegni_scena', -1)) >= 0 else '-')
            scena[k] += 1
            # il colore riportato e' quello dell'immagine precedente
            c = d.get('colore_centro_immagine_prima', '-')
            if ultimo and re.match(r'^[\d.]+,[\d.]+,[\d.]+$', c):
                colori[ultimo].append(sum(float(x) for x in c.split(',')) / 3)
            ultimo = k[0]
            if a.linea:
                print('%s %-32s scena=%-4s faro_dopo=%-4s camera=%-24s colore_prima=%s' % (
                    ora(t), d.get('nuvola', '?'), d.get('disegni_scena', '?'), d.get('faro_dopo_disegni_scena', '?'),
                    d.get('camera_al_faro', '-'), c))

    print('\nrighe per tipo: ' + ', '.join('%s %d' % kv for kv in sorted(tipi.items())))
    if viste:
        print('\nviste:')
        for o, v in viste:
            print('  %s %s' % (o, v))
    if scena:
        print('\nimmagini in pianta/sezione (nuvola, disegni di Revit nella scena, faro):')
        for k, n in scena.most_common():
            print('  %5d  %-32s %-7s %s' % (n, k[0], k[1], k[2]))
    if colori:
        print('\ngrigio medio al centro della scena per tipo di immagine (voce 304):')
        for k, v in sorted(colori.items()):
            v = sorted(v)
            print('  %-32s %5d immagini, mediana %.1f, 10%% %.1f, 90%% %.1f' % (k, len(v), v[len(v) // 2], v[len(v) // 10], v[len(v) * 9 // 10]))
    if salti:
        print('\nsegnali senza disegno: ' + ', '.join('%s %d' % kv for kv in salti.most_common()))
    if eventi:
        print('\neventi:')
        for k, n in eventi.most_common(30):
            print('  %5d  %s' % (n, k))
    return 0


if __name__ == '__main__':
    sys.exit(main())
