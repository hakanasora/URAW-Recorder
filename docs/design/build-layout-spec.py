"""Render a static planning figure. This is not a WinUI implementation or screenshot."""
from pathlib import Path
import math
from PIL import Image, ImageDraw, ImageFont, ImageFilter
from legacy_vu import render_vu
from monitor_plots import draw_spectrum, draw_pitch

ROOT = Path(__file__).parent
S = 4
W, H = 1260, 1240
im = Image.new('RGB', (W*S, H*S), '#E9EAED')
d = ImageDraw.Draw(im)
FONTS = Path('C:/Windows/Fonts')

def font(size, bold=False, cjk=False):
    name = 'msyhbd.ttc' if cjk and bold else 'msyh.ttc' if cjk else 'segoeuib.ttf' if bold else 'segoeui.ttf'
    return ImageFont.truetype(str(FONTS/name), round(size*S))

def rect(x,y,w,h,fill,outline=None,r=0):
    box = [round(x*S),round(y*S),round((x+w)*S),round((y+h)*S)]
    if r: d.rounded_rectangle(box, radius=round(r*S), fill=fill, outline=outline, width=S)
    else: d.rectangle(box, fill=fill, outline=outline, width=S)

def text(x,y,s,size=13,color='#F5F5F7',bold=False,cjk=False):
    d.text((round(x*S),round(y*S)),s,font=font(size,bold,cjk),fill=color)

def line(points,color,width=1):
    d.line([(round(x*S),round(y*S)) for x,y in points],fill=color,width=max(1,round(width*S)))

def pause(x,y):
    rect(x,y,4,14,'#F5F5F7',r=.6); rect(x+8,y,4,14,'#F5F5F7',r=.6)

def play(x,y):
    d.polygon([(round(x*S),round(y*S)),(round(x*S),round((y+14)*S)),(round((x+12)*S),round((y+7)*S))], fill='#F5F5F7')

def reset_icon(x,y):
    d.arc((round(x*S),round(y*S),round((x+14)*S),round((y+14)*S)),215,520,fill='#86868B',width=2*S)
    line([(x,y+1),(x,y+6),(x+5,y+6)],'#86868B',1.5)

_legacy_face = None

def centered_text(x,cy,s,size=13,color='#F5F5F7',bold=False,cjk=False):
    f=font(size,bold,cjk)
    box=d.textbbox((0,0),s,font=f)
    d.text((round(x*S),round(cy*S-(box[1]+box[3])/2)),s,font=f,fill=color)

def vu(x,y,name):
    global _legacy_face
    if _legacy_face is None: _legacy_face=render_vu(S,-1.0)
    im.paste(_legacy_face,(round(x*S),round(y*S)),_legacy_face)
    centered_text(x+126,y+175,name,12,'#86868B')

def selector(x,y,current=0):
    rect(x,y,240,36,'#0A0A0A',r=4)
    rect(x+4+current*78,y+4,76,28,'#343437',r=3)
    for i,label in enumerate(['电平','频谱','音高']):
        centered_text(x+29+i*78,y+18,label,13,'#F5F5F7' if i==current else '#86868B',cjk=True)

def inspector(x,y):
    # Draw to an isolated surface so the final fields are clipped by its viewport.
    global im,d
    original,original_draw=im,d
    pane=Image.new('RGB',(264*S,384*S),'#252527')
    im,d=pane,ImageDraw.Draw(pane)
    rect(20,12,84,28,'#0A0A0A',r=3)
    rect(112,12,84,28,'#343437',r=3)
    text(40,17,'INPUT',12,'#A1A1A6',True)
    text(139,17,'FILE',12,bold=True)
    line([(213,17),(225,29)],'#747476',1.4); line([(225,17),(213,29)],'#747476',1.4)
    for title,yy,value in [('保存路径',64,'选择文件夹'),('录制格式',152,'FLAC'),('压缩品质',240,'标准 · 等级 5')]:
        text(24,yy,title,13,'#A1A1A6',cjk=True)
        if title=='保存路径':
            rect(24,yy+28,172,36,'#49494D',r=3)
            rect(204,yy+28,36,36,'#49494D',r=3)
            text(36,yy+36,value,12,'#BDBDC2',cjk=True)
            line([(213,yy+40),(213,yy+52),(229,yy+52),(229,yy+42),(220,yy+42),(218,yy+39),(213,yy+40)],'#BDBDC2',1.2)
            continue
        rect(24,yy+28,216,36,'#49494D',r=3)
        text(36,yy+36,value,13,'#BDBDC2',cjk=title=='压缩品质')
        line([(216,yy+44),(221,yy+49),(226,yy+44)],'#F5F5F7',1.3)
    text(24,312,'无损压缩，等级影响体积与耗时。',12,'#A1A1A6',cjk=True)
    text(24,334,'停止后可更改',12,'#A1A1A6',cjk=True)
    im,d=original,original_draw
    im.paste(pane,(round(x*S),round(y*S)))

def window(x,y,expanded=False,paused=False,mode='level'):
    width=1084 if expanded else 820
    rect(x,y,width,416,'#1B1B1B','#707075')
    text(x+16,y+7,'Portable Recorder · UI Preview',12,'#86868B')
    # Schematic caption glyphs only; the real app will retain Windows caption controls.
    text(x+width-116,y+4,'—',14,'#A1A1A6')
    text(x+width-76,y+5,'□',14,'#A1A1A6')
    text(x+width-36,y+3,'×',17,'#A1A1A6')
    off=264 if expanded else 0
    if expanded: inspector(x,y+32)
    mx=x+off
    for yy in [64,70,76]: line([(mx+39,y+yy),(mx+57,y+yy)],'#86868B',1.5)
    centered_text(mx+76,y+70,'USB Audio Device',13)
    selector(mx+290,y+52,['level','spectrum','pitch'].index(mode))
    if mode=='level':
        peakcolor='#FF3037' if paused else '#86868B'
        text(mx+28,y+259,'PEAK +0.8 dBFS' if paused else 'PEAK −18.6 dBFS',14,peakcolor)
        reset_icon(mx+184,y+263)
        rect(mx+28,y+183,18,5,'#F5F5F7')
        text(mx+50,y+138,'32.0',69,bold=True)
        text(mx+28,y+227,'dBFS · RMS',12,'#86868B')
        # Current overload indicator is hidden while normal; no unexplained dot.
        vu(mx+256,y+120,'L'); vu(mx+532,y+120,'R')
    elif mode=='spectrum':
        draw_spectrum(im,mx,y+96,S)
    else:
        draw_pitch(im,mx,y,S)
    rect(mx,y+332,820,84,'#0E0E0E')
    line([(mx,y+332),(mx+820,y+332)],'#323234')
    d.ellipse(((mx+28)*S,(y+365)*S,(mx+45)*S,(y+382)*S),outline='#6E6E73',width=3)
    line([(mx+36.5,y+364),(mx+36.5,y+371)],'#F5F5F7',1.5)
    text(mx+53,y+362,'0 dB',13,'#9D9D9F')
    if paused:
        text(mx+392,y+344,'已暂停',12,'#9D9D9F',cjk=True)
        text(mx+350,y+364,'00:13.95',32)
        rect(mx+647,y+353,100,42,'#1D1D1F',r=5)
        rect(mx+744,y+353,3,42,'#1D1D1F')
        play(mx+665,y+367); text(mx+685,y+362,'继续',14,bold=True,cjk=True)
        rect(mx+750,y+353,42,42,'#1D1D1F',r=5)
        rect(mx+750,y+353,3,42,'#1D1D1F')
        rect(mx+766,y+369,10,10,'#FF3037',r=1)
    else:
        rect(mx+692,y+353,100,42,'#D3222B',r=5)
        rect(mx+712,y+369,10,10,'#F5F5F7',r=5)
        text(mx+731,y+362,'录制',14,bold=True,cjk=True)

text(42,26,'Portable Recorder · 原生窗体布局规格',25,'#24242A',True,True)
text(42,65,'结构与配色示意 / 非 WinUI 运行截图 / 不能据此判断原生字体渲染',14,'#55555D',cjk=True)
text(42,98,'A  默认待机：客户区 820 × 416 DIP',16,'#24242A',True,True)
window(42,130)
text(902,154,'产品原点保持',17,'#24242A',True,True)
for i,s in enumerate(['深色、克制、仪器感','三种模式直接可见','当前值先于历史摘要','唯一强动作：录制']):
    text(902,190+i*29,s,14,'#55555D',cjk=True)
text(902,336,'位置与语义稳定',17,'#24242A',True,True)
for i,s in enumerate(['配置入口移到头部','录制／停止原位切换','活动按钮组总宽 145','过载时才显示明确提示']):
    text(902,372+i*29,s,14,'#55555D',cjk=True)
text(42,586,'B  暂停＋FILE 检查器：客户区 1084 × 416 DIP',16,'#24242A',True,True)
window(42,618,True,True)
text(42,1060,'264 DIP 检查器',15,'#24242A',True,True)
text(340,1060,'820 DIP 主工作区保持尺寸；展开不会把 VU 挤小',15,'#24242A',True,True)
text(42,1101,'精确颜色、字体角色、交互状态、低空间覆盖模式和实现顺序见 execution-plan.md。',14,'#55555D',cjk=True)
text(42,1134,'示意中的 14 DIP 白字录制采用对比度修正底色 #D3222B；停止／过载保留 #FF3037。',14,'#55555D',cjk=True)
text(42,1167,'VU 按既有绘制公式重绘；路径框与浏览按钮各自保留完整四角。原生字体待实窗评审。',13,'#55555D',cjk=True)
im.resize((W*2,H*2),Image.Resampling.LANCZOS).save(ROOT/'layout-spec.png')
print(ROOT/'layout-spec.png')
