"""Static spectrum/pitch examples retaining the established recorder.js visuals."""
import math
from PIL import Image, ImageDraw, ImageFont, ImageFilter, ImageChops

GREY='#86868B'; GOLD='#FFB700'

def draw_spectrum(im,x,y,s=2):
    w,h=820,236; left,right,top,bottom=46,798,12,196
    layer=Image.new('RGBA',(w*s,h*s)); d=ImageDraw.Draw(layer)
    def ln(points,color,width=1): d.line([(round(a*s),round(b*s)) for a,b in points],fill=color,width=max(1,round(width*s)))
    xf=lambda f:left+math.log10(f/20)/3*(right-left)
    yf=lambda db:top+(6-db)/96*(bottom-top)
    freqs=[20,50,100,200,500,1000,2000,5000,10000,20000]
    levels=[-12,-30,-48,-66,-84]
    for i in range(22):
        db=-88.5+i*4.5
        ln([(left,yf(db)),(right,yf(db))],(41,41,44,115),.65)
    for decade in [10,100,1000,10000]:
        for part in range(2,20):
            f=decade*part/2
            if 20<=f<=20000: ln([(xf(f),top),(xf(f),bottom)],(41,41,44,120),.65)
    for db in levels: ln([(left,yf(db)),(right,yf(db))],(50,50,52,195),.8)
    for f in freqs: ln([(xf(f),top),(xf(f),bottom)],(50,50,52,195),.8)
    points=[]
    for i in range(753):
        u=i/752
        amplitude=.14+.11*math.exp(-((u-.14)/.12)**2)+.48*math.exp(-((u-.49)/.24)**2)+.16*math.exp(-((u-.79)/.12)**2)
        amplitude+=.018*math.sin(u*33)+.009*math.cos(u*52)
        points.append((left+u*(right-left),yf(-84+amplitude*72)))
    fill=Image.new('RGBA',layer.size); fd=ImageDraw.Draw(fill)
    for xx,yy in points:
        depth=min((bottom-top)*.46,bottom-yy)
        for py in range(round(yy*s),round((yy+depth)*s)):
            t=(py/s-yy)/depth
            alpha=round(195*max(0,1-t)**2)
            alpha=round(alpha*(.87+.13*math.cos((xx-left)*math.pi)))
            fd.line((round(xx*s),py,round((xx+1)*s),py),fill=(255,183,0,alpha))
    layer=Image.alpha_composite(layer,fill)
    glow=Image.new('RGBA',layer.size)
    ImageDraw.Draw(glow).line([(round(a*s),round(b*s)) for a,b in points],fill=(255,183,0,95),width=4*s)
    layer=Image.alpha_composite(layer,glow.filter(ImageFilter.GaussianBlur(5*s)))
    d=ImageDraw.Draw(layer); ln(points,GOLD,1.8)
    mask=Image.new('L',layer.size,0); md=ImageDraw.Draw(mask)
    for px in range(left*s,right*s):
        u=(px/s-left)/(right-left)
        a=min(1,u/.05,(1-u)/.05)
        md.line((px,top*s,px,bottom*s),fill=round(255*max(0,a)))
    layer.putalpha(ImageChops.multiply(layer.getchannel('A'),mask))
    im.paste(layer,(round(x*s),round(y*s)),layer)
    d=ImageDraw.Draw(im); font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',12*s)
    def txt(xx,yy,t,anchor='lt'): d.text(((x+xx)*s,(y+yy)*s),t,font=font,fill=GREY,anchor=anchor)
    for db in levels: txt(left-11,yf(db),str(db),'rm')
    txt(left-11,top-1,'dB','rb')
    for i,f in enumerate(freqs):
        txt(xf(f),bottom+22,f'{f//1000}k' if f>=1000 else str(f),'ls' if i==0 else 'rs' if i==len(freqs)-1 else 'ms')
    txt(right,h-5,'Hz','rs')

def draw_pitch(im,x,y,s=2):
    # Left-hand readout and straight, fading cents ruler.
    d=ImageDraw.Draw(im)
    def txt(xx,yy,t,size=12,color=GREY,anchor='lt',cjk=False):
        f=ImageFont.truetype('C:/Windows/Fonts/msyh.ttc' if cjk else 'C:/Windows/Fonts/segoeui.ttf',round(size*s))
        d.text(((x+xx)*s,(y+yy)*s),t,font=f,fill=color,anchor=anchor)
    txt(28,211,'B',69,'#F5F5F7',anchor='ls'); txt(68,211,'2',36,'#F5F5F7',anchor='ls')
    txt(28,227,'123.47',20,'#F5F5F7'); txt(91,235,'Hz',12)
    ruler=Image.new('RGBA',(200*s,50*s)); rd=ImageDraw.Draw(ruler)
    rf=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',12*s)
    for val in range(-50,51,5):
        xx=7+(val+50)/100*186
        fade=min(1,xx/54,(200-xx)/54)
        major=val%25==0
        rd.line((xx*s,(6 if major else 12)*s,xx*s,24*s),fill=(134,134,139,round(255*max(0,fade)*(.85 if major else .55))),width=s)
        if major:
            label='0' if val==0 else f'+{val}' if val>0 else f'−{abs(val)}'
            rd.text((xx*s,35*s),label,font=rf,fill=(134,134,139,round(255*max(0,fade))),anchor='mt')
    rd.line((100*s,3*s,100*s,27*s),fill=GOLD,width=2*s)
    im.paste(ruler,(round((x+28)*s),round((y+266)*s)),ruler)
    # Right-hand four-second history; 36 semitones, fixed span.
    gx,gy=256,96; left,right,top,bottom=32,528,16,208
    plot=Image.new('RGBA',(536*s,236*s)); pd=ImageDraw.Draw(plot)
    yf=lambda midi:top+(72-midi)/36*(bottom-top)
    for midi in range(36,73,3):
        yy=yf(midi)
        col=(50,50,52,220) if midi%12==0 else (41,41,44,150)
        pd.line((left*s,yy*s,right*s,yy*s),fill=col,width=s)
    points=[]
    for i in range(260):
        u=i/259
        midi=(53+.14*math.sin(u*95)) if u<.19 else (47+.16*math.sin(u*112)+.08*math.cos(u*79))
        if .19<=u<.26: continue
        xx=left+u*(right-left)*.94
        if i and .26<=u<.265: points=[]
        points.append((xx,yf(midi)))
        if len(points)>1:
            fade=1 if u<.76 else max(0,(1-u)/.24)
            pd.line(tuple(a*s for pt in points[-2:] for a in pt),fill=(255,183,0,round(255*fade)),width=max(1,round(1.45*s)))
    # Feather graph only, then place fully readable axis labels.
    alpha=plot.getchannel('A'); ap=alpha.load()
    for px in range(round((left+.76*(right-left))*s),right*s):
        fade=max(0,(right-px/s)/((right-left)*.24))
        for py in range(top*s,(bottom+1)*s): ap[px,py]=round(ap[px,py]*fade)
    plot.putalpha(alpha)
    im.paste(plot,(round((x+gx)*s),round((y+gy)*s)),plot)
    d.line(((x+gx+left)*s,(y+gy+top)*s,(x+gx+left)*s,(y+gy+bottom)*s),fill=GREY,width=s)
    for midi in [36,43,48,55,60,67,72]:
        label=('C' if midi%12==0 else 'G')+str(midi//12-1)
        txt(gx+left-8,gy+yf(midi),label,anchor='rm')
    txt(gx+left,gy+222,'−4 s'); txt(gx+right,gy+222,'现在',anchor='rt',cjk=True)
