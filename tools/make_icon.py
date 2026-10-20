"""Gera o icone arco-iris do Freedraw a partir do traco preto original.

uso: python tools/make_icon.py tools/icon-source.png src/Freedraw.App/Assets

Saidas: freedraw.ico (16..64 em BMP, 256 em PNG -- o formato que o Windows le
em todo lugar), freedraw.png (512) e preview_sizes.png (conferencia visual).
"""
import colorsys
import io
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter

SRC, OUT = Path(sys.argv[1]), Path(sys.argv[2])
OUT.mkdir(parents=True, exist_ok=True)

STROKE = 21.0                 # espessura medida do traco a 512 px
CROP = (10, 10, 502, 502)     # bbox do desenho (20,39)-(492,473) + folga, quadrado
SIDE = CROP[2] - CROP[0]

alpha = Image.open(SRC).convert("RGBA").getchannel("A").crop(CROP)


def rainbow(side: int) -> np.ndarray:
    """Arco-iris na diagonal de baixo-esquerda (vermelho) para cima-direita
    (violeta): o eixo do lapis, que assim pega o espectro inteiro."""
    y, x = np.mgrid[0:side, 0:side].astype(np.float32)
    proj = x - y
    # Normalizado pela faixa que o TRACO ocupa, nao pelo quadrado inteiro:
    # senao vermelho e violeta caem nos cantos vazios e sobra so verde.
    ink = np.array(alpha) > 0
    lo, hi = proj[ink].min(), proj[ink].max()
    t = np.clip((proj - lo) / (hi - lo), 0, 1)
    conv = np.array([colorsys.hsv_to_rgb(h, 0.85, 0.97) for h in np.linspace(0, 0.80, 1024)])
    rgb = conv[(t * 1023).astype(int)]
    return (rgb * 255).astype(np.uint8)


GRADIENT = rainbow(SIDE)


def render(size: int) -> Image.Image:
    # Traco minimo em pixels de saida: abaixo de ~1.6 px a linha vira poeira
    # na bandeja. A espessura extra entra por dilatacao ANTES de reduzir.
    k = size / SIDE
    target = 1.7 if size <= 24 else 2.2 if size <= 32 else 2.6 if size <= 48 else 0
    mask = alpha
    grow = int(round(target / k - STROKE)) if target else 0
    if grow > 0:
        n = grow + 1 if grow % 2 == 0 else grow
        mask = mask.filter(ImageFilter.MaxFilter(n | 1))
    rgba = np.dstack([GRADIENT, np.array(mask)])
    img = Image.fromarray(rgba, "RGBA")
    return img.resize((size, size), Image.LANCZOS) if size != SIDE else img


def bmp_entry(img: Image.Image) -> bytes:
    """Frame BMP de ICO: BITMAPINFOHEADER com altura dobrada, BGRA de baixo
    para cima e mascara AND zerada (o alfa manda)."""
    w, h = img.size
    a = np.array(img)
    bgra = a[::-1, :, [2, 1, 0, 3]].tobytes()
    and_row = ((w + 31) // 32) * 4
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(bgra) + and_row * h, 0, 0, 0, 0)
    return header + bgra + bytes(and_row * h)


def png_entry(img: Image.Image) -> bytes:
    buf = io.BytesIO()
    img.save(buf, "PNG", optimize=True)
    return buf.getvalue()


sizes = [16, 20, 24, 32, 40, 48, 64, 256]
frames = [(s, png_entry(render(s)) if s >= 256 else bmp_entry(render(s))) for s in sizes]

ico = io.BytesIO()
ico.write(struct.pack("<HHH", 0, 1, len(frames)))
offset = 6 + 16 * len(frames)
for s, data in frames:
    dim = 0 if s >= 256 else s
    ico.write(struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset))
    offset += len(data)
for _, data in frames:
    ico.write(data)
(OUT / "freedraw.ico").write_bytes(ico.getvalue())

big = Image.new("RGBA", (512, 512))
big.paste(render(SIDE), (CROP[0], CROP[1]))
big.save(OUT / "freedraw.png", optimize=True)

# folha de previa: cada tamanho sobre fundo claro e escuro, ampliado sem suavizar
sheet = Image.new("RGBA", (len(sizes[:-1]) * 70 + 10, 150), (255, 255, 255, 255))
for i, s in enumerate(sizes[:-1]):
    for row, bg in enumerate([(243, 243, 243, 255), (32, 32, 32, 255)]):
        tile = Image.new("RGBA", (s, s), bg)
        tile.alpha_composite(render(s))
        z = tile.resize((64, 64), Image.NEAREST) if s < 64 else tile
        sheet.paste(z, (10 + i * 70, 10 + row * 70))
sheet.save(OUT / "preview_sizes.png")
print("ok", [(s, len(d)) for s, d in frames])
