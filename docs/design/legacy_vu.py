"""Static drawing port of prototype/recorder.js drawVu, not a new dial design.

Preserves the original two Bezier light beams, soft lamp sources, reflection,
graduations, needle and hub. Used only by documentation preview rendering.
"""
import math
from PIL import Image, ImageDraw, ImageFont, ImageFilter, ImageChops


def render_vu(scale=2, value=-1.0):
    w, h, cx, cy, r = 260, 159, 130, 150, 104
    sz = (w * scale, h * scale)
    accent, amber, ivory = (255,183,0), (255,150,0), (255,230,175)
    positive = (243,139,88)
    face = Image.new('RGBA', sz)

    def rgba(rgb, alpha=1):
        return (*rgb, round(255*alpha))

    def interpolate(stops, t):
        t = max(0,min(1,t))
        for (a,c),(b,e) in zip(stops,stops[1:]):
            if t <= b:
                f=(t-a)/(b-a)
                return tuple(round(v+(z-v)*f) for v,z in zip(c,e))
        return stops[-1][1]

    def radial(x,y,rx,ry,stops):
        layer=Image.new('RGBA',sz)
        pix=layer.load()
        for py in range(max(0,int((y-ry)*scale)), min(h*scale,int((y+ry)*scale)+1)):
            for px in range(max(0,int((x-rx)*scale)), min(w*scale,int((x+rx)*scale)+1)):
                t=math.hypot((px/scale-x)/rx,(py/scale-y)/ry)
                if t<1: pix[px,py]=interpolate(stops,t)
        return layer

    def bezier(a,b,c,e):
        return [((1-t)**3*a[0]+3*(1-t)**2*t*b[0]+3*(1-t)*t*t*c[0]+t**3*e[0],
                 (1-t)**3*a[1]+3*(1-t)**2*t*b[1]+3*(1-t)*t*t*c[1]+t**3*e[1])
                for t in (i/48 for i in range(49))]

    face=Image.alpha_composite(face,radial(cx,cy-r*.28,r*1.02,r*.71,
        [(0,rgba(amber,.14)),(.45,rgba(amber,.095)),(1,rgba(amber,0))]))
    for side in (-1,1):
        x=cx+side*r*.38
        a=(x-r*.12,cy+3); b=(x-r*.54,cy-r*.72)
        c=(x+r*.54,cy-r*.72); e=(x+r*.12,cy+3)
        points=(bezier(a,(x-r*.24,cy-r*.12),(x-r*.70,cy-r*.42),b)
            +bezier(b,(x-r*.30,cy-r*.91),(x+r*.30,cy-r*.91),c)
            +bezier(c,(x+r*.70,cy-r*.42),(x+r*.24,cy-r*.12),e))
        mask=Image.new('L',sz)
        ImageDraw.Draw(mask).polygon([(round(px*scale),round(py*scale)) for px,py in points],fill=255)
        beam=Image.new('RGBA',sz)
        bd=ImageDraw.Draw(beam)
        stops=[(0,rgba(accent,.96)),(.12,rgba(amber,.86)),(.38,rgba(amber,.53)),
               (.72,rgba(amber,.19)),(1,rgba(amber,0))]
        for py in range(h*scale):
            color=interpolate(stops,(cy+2-py/scale)/(r*.84+2))
            bd.line((0,py,w*scale,py),fill=color)
        beam.putalpha(ImageChops.multiply(beam.getchannel('A'),mask))
        face=Image.alpha_composite(face,beam.filter(ImageFilter.GaussianBlur(r*.065*scale)))
        face=Image.alpha_composite(face,radial(x,cy-3,r*.19,r*.13,
            [(0,rgba(ivory)),(.14,rgba(accent,.98)),(.42,rgba(amber,.76)),
             (.72,rgba(amber,.23)),(1,rgba(amber,0))]))
    fade=Image.new('L',sz,255); fd=ImageDraw.Draw(fade)
    for py in range(int((cy-8)*scale),h*scale):
        t=(py/scale-(cy-8))/15
        alpha=255 if t<=.44 else round(255*max(0,(1-t)/.56))
        fd.line((0,py,w*scale,py),fill=alpha)
    face.putalpha(ImageChops.multiply(face.getchannel('A'),fade))

    def segment(layer,points,color,width=1):
        ImageDraw.Draw(layer).line([(round(x*scale),round(y*scale)) for x,y in points],
            fill=color,width=max(1,round(width*scale)))

    def arc(layer,rad,start,end,color,width=1):
        pts=[(cx+rad*math.cos(math.radians(start+(end-start)*i/150)),
              cy+rad*math.sin(math.radians(start+(end-start)*i/150))) for i in range(151)]
        segment(layer,pts,color,width)

    reflection=Image.new('RGBA',sz)
    arc(reflection,r+16,-153,-27,rgba(ivory,.30),.8)
    face=Image.alpha_composite(face,reflection)
    dial=Image.new('RGBA',sz)
    arc(dial,r,-153,-48,rgba(ivory,.65))
    arc(dial,r,-48,-27,rgba(positive))
    for i in range(37):
        a=math.radians(-153+i*126/36)
        segment(dial,[(cx+(r-4)*math.cos(a),cy+(r-4)*math.sin(a)),
                      (cx+r*math.cos(a),cy+r*math.sin(a))],rgba(positive if a>math.radians(-48) else ivory,.86))
    marks=[-20,-10,-7,-5,-3,0,3]; angles=[-153,-132,-111,-90,-69,-48,-27]
    dd=ImageDraw.Draw(dial)
    small=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',11*scale)
    vu_font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',16*scale)
    for mark,deg in zip(marks,angles):
        a=math.radians(deg); color=positive if mark>0 else ivory
        segment(dial,[(cx+(r-8)*math.cos(a),cy+(r-8)*math.sin(a)),
                      (cx+r*math.cos(a),cy+r*math.sin(a))],rgba(color))
        label=f'+{mark}' if mark>0 else str(mark)
        dd.text(((cx+(r+14)*math.cos(a))*scale,(cy+(r+14)*math.sin(a)+4)*scale),
                label,font=small,fill=rgba(color),anchor='ms')
    dd.text(((cx+24)*scale,(cy-24)*scale),'VU',font=vu_font,fill=rgba(ivory),anchor='ms')
    shadow=dial.copy(); shadow.putalpha(dial.getchannel('A').point(lambda a:round(a*.50)))
    face=Image.alpha_composite(face,shadow.filter(ImageFilter.GaussianBlur(2.3*scale)))
    face=Image.alpha_composite(face,dial)

    value=max(-20,min(3,value)); i=0
    while i<len(marks)-2 and value>marks[i+1]: i+=1
    a=math.radians(angles[i]+(value-marks[i])/(marks[i+1]-marks[i])*(angles[i+1]-angles[i]))
    tip=(cx+(r-5)*math.cos(a),cy+(r-5)*math.sin(a))
    needle=Image.new('RGBA',sz)
    segment(needle,[(cx,cy),tip],rgba(accent,.65),2.5)
    face=Image.alpha_composite(face,needle.filter(ImageFilter.GaussianBlur(3*scale)))
    def rotate(px,py): return ((cx+px*math.cos(a)-py*math.sin(a))*scale,(cy+px*math.sin(a)+py*math.cos(a))*scale)
    nd=ImageDraw.Draw(needle)
    nd.polygon([rotate(-3,-1.4),rotate(r-5,-.25),rotate(r-5,.25),rotate(-3,1.4)],fill=rgba(accent))
    segment(needle,[(cx,cy),tip],rgba(ivory,.8),.65)
    face=Image.alpha_composite(face,needle)
    hub=Image.new('RGBA',sz)
    ImageDraw.Draw(hub).ellipse(((cx-6.7)*scale,(cy-6.7)*scale,(cx+6.7)*scale,(cy+6.7)*scale),fill='#0C0D0D')
    hp=hub.load()
    for py in range(round((cy-5.3)*scale),round((cy+5.3)*scale)+1):
        for px in range(round((cx-5.3)*scale),round((cx+5.3)*scale)+1):
            if math.hypot(px/scale-cx,py/scale-cy)<=5.3:
                t=min(1,math.hypot(px/scale-(cx-1.8),py/scale-(cy-2.2))/8)
                hp[px,py]=interpolate([(0,rgba(ivory)),(.32,rgba(accent)),(.8,rgba(amber)),
                                       (1,rgba(tuple(round(v*.67) for v in amber)))],t)
    return Image.alpha_composite(face,hub)
