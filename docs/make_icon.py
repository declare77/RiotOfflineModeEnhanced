from PIL import Image, ImageDraw, ImageFilter
import sys
N=1024
def render(ring=True, small=False):
    m = 0 if small else 24
    rad = 200 if small else 230
    k = 1.22 if small else 1.0
    def sc(x,y,r): return (512+(x-512)*k, 512+(y-512)*k, r*k)
    # background tile with vertical gradient
    bg=Image.new('RGBA',(N,N),(0,0,0,0))
    grad=Image.new('RGBA',(N,N))
    gd=ImageDraw.Draw(grad)
    top=(0x22,0x27,0x31); bot=(0x0C,0x0E,0x12)
    for y in range(N):
        t=y/(N-1); gd.line([(0,y),(N,y)],fill=tuple(int(top[i]+(bot[i]-top[i])*t) for i in range(3))+(255,))
    mask=Image.new('L',(N,N),0); ImageDraw.Draw(mask).rounded_rectangle([m,m,N-m,N-m],radius=rad,fill=255)
    bg.paste(grad,(0,0),mask)
    # subtle inner border
    ImageDraw.Draw(bg).rounded_rectangle([m,m,N-m,N-m],radius=rad,outline=(255,255,255,22),width=6)
    # crescent = big circle minus offset circle
    cm=Image.new('L',(N,N),0); d=ImageDraw.Draw(cm)
    cx,cy,r=sc(470,520,318)
    d.ellipse([cx-r,cy-r,cx+r,cy+r],fill=255)
    ox,oy,orr=sc(610,410,262)
    d.ellipse([ox-orr,oy-orr,ox+orr,oy+orr],fill=0)
    coral=Image.new('RGBA',(N,N),(0xFF,0x4F,0x5E,255))
    # soft glow behind crescent
    glow=Image.new('RGBA',(N,N),(0xFF,0x4F,0x5E,0)); glow.putalpha(cm.filter(ImageFilter.GaussianBlur(40)).point(lambda v:int(v*0.35)))
    bg=Image.alpha_composite(bg,glow)
    bg.paste(coral,(0,0),cm)
    if ring:
        rc=(735,742); R=118; w=46
        d2=ImageDraw.Draw(bg)
        # knock-out gap so the ring reads cleanly over the crescent
        d2.ellipse([rc[0]-R-30,rc[1]-R-30,rc[0]+R+30,rc[1]+R+30],fill=(0x10,0x12,0x17,255))
        d2.ellipse([rc[0]-R,rc[1]-R,rc[0]+R,rc[1]+R],outline=(0xEC,0xEE,0xF2,255),width=w)
    return bg
big=render(True)
big.save('icon-1024.png')
small=render(False, small=True)  # bigger crescent, no ring, for the tiniest sizes
sizes=[256,128,64,48,32,24,16]
imgs=[]
for s in sizes:
    src = big if s>=32 else small
    imgs.append(src.resize((s,s),Image.LANCZOS))
for s,im in zip(sizes,imgs): im.save(f'icon-{s}.png')
imgs[0].save(sys.argv[1], format='ICO', sizes=[(s,s) for s in sizes], append_images=imgs[1:])

# --- Write a classic ICO: 32-bit BMP frames for <=128px (best compatibility with
# System.Drawing.Icon on .NET Framework), PNG only for the 256px frame.
import io, struct
def bmp_frame(im):
    w,h=im.size; px=im.convert('RGBA').load()
    xor=bytearray()
    for y in range(h-1,-1,-1):
        for x in range(w):
            r,g,b,a=px[x,y]; xor+=bytes((b,g,r,a))
    row=((w+31)//32)*4
    andmask=bytearray(row*h)  # all 0 = use alpha channel
    hdr=struct.pack('<IiiHHIIiiII',40,w,h*2,1,32,0,len(xor)+len(andmask),0,0,0,0)
    return hdr+bytes(xor)+bytes(andmask)
def png_frame(im):
    b=io.BytesIO(); im.save(b,'PNG'); return b.getvalue()
frames=[(s,(png_frame(im) if s>=256 else bmp_frame(im))) for s,im in zip(sizes,imgs)]
frames.sort(key=lambda f:f[0])
out=bytearray(struct.pack('<HHH',0,1,len(frames)))
offset=6+16*len(frames); data=bytearray()
for s,f in frames:
    out+=struct.pack('<BBBBHHII',s%256,s%256,0,0,1,32,len(f),offset+len(data)); data+=f
open(sys.argv[1],'wb').write(bytes(out+data))
