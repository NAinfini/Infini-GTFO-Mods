"""Read-only validation of exported production PNGs."""
from pathlib import Path
from PIL import Image
root = Path(__file__).resolve().parents[1]
files = list((root / 'assets/marker-icons').glob('*.png'))
assert len(files) == 58
for path in files:
    image = Image.open(path)
    assert image.mode == 'RGBA' and image.size == (256, 256), path.name
    alpha = image.getchannel('A')
    assert alpha.getextrema() == (0, 255), path.name
    x0,y0,x1,y1 = alpha.getbbox()
    assert min(x0,y0) > 0 and max(x1,y1) < 256, (path.name,'clipped boundary')
    bx0,by0,bx1,by1 = alpha.point(lambda a: 255 if a >= 128 else 0).getbbox()
    assert abs(max(bx1-bx0,by1-by0)-224) <= 2, (path.name,'visible size differs')
    assert abs((bx0+bx1)/2-128) <= 1 and abs((by0+by1)/2-128) <= 1, (path.name,'visible content off center')
    assert sum(alpha.histogram()[200:]) > 256*256*.03, (path.name,'empty icon')
print(f'PASS: {len(files)} PNGs: 256px RGBA, true transparency, opaque content, clear edges.')
