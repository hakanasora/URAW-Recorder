"""Generate three equal-size static previews, without running any app/browser."""
from pathlib import Path
import importlib.util
from PIL import Image, ImageDraw

root=Path(__file__).parent
spec=importlib.util.spec_from_file_location('layout',root/'build-layout-spec.py')
layout=importlib.util.module_from_spec(spec)
spec.loader.exec_module(layout)
for mode in ('level','spectrum','pitch'):
    layout.im=Image.new('RGB',(820*layout.S,416*layout.S),'#1B1B1B')
    layout.d=ImageDraw.Draw(layout.im)
    layout.window(0,0,mode=mode)
    path=root/f'preview-{mode}.png'
    layout.im.resize((1640,832),Image.Resampling.LANCZOS).save(path)
    print(path)
layout.im=Image.new('RGB',(264*layout.S,384*layout.S),'#252527')
layout.d=ImageDraw.Draw(layout.im)
layout.inspector(0,0)
layout.im.crop(tuple(v*layout.S for v in (16,56,248,140))).resize(
    (464,168),Image.Resampling.LANCZOS).save(root/'preview-file-path.png')
