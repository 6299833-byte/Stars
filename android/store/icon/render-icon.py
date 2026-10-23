import numpy as np
from PIL import Image, ImageDraw, ImageFilter
OUT='/home/user/Stars/android/store/icon/'
rng=np.random.default_rng(7)
def hexc(h): return np.array([int(h[i:i+2],16) for i in (1,3,5)],float)/255
SPACE=hexc('#020309')

def noise1(x, seed, freq):
    # smooth 1D value noise
    r=np.random.default_rng(seed).random(4096)
    t=x*freq; i=np.floor(t).astype(int)%4095; f=t-np.floor(t); f=f*f*(3-2*f)
    return r[i]*(1-f)+r[i+1]*f

def ring_rgba(r):
    n=noise1(r,1,180)*0.6+noise1(r,2,900)*0.4
    col=np.zeros(r.shape+(3,)); a=np.zeros(r.shape)
    def put(m,c,al):
        col[m]=c[m] if c.ndim>1 else c; a[m]=al[m] if np.ndim(al) else al
    def mix(c1,c2,t): c1=np.array(c1)/255; c2=np.array(c2)/255; return c1+(c2-c1)*t[...,None]
    m=(r>=1.11)&(r<1.236); put(m,np.broadcast_to(np.array([120,110,95])/255,r.shape+(3,)),0.04+0.03*n)
    m=(r>=1.236)&(r<1.526); put(m,mix([120,108,92],[165,150,125],n),0.1+0.14*n)
    m=(r>=1.526)&(r<1.95); pos=np.clip((r-1.526)/0.424*2.5,0,1); pos=pos*pos*(3-2*pos)
    put(m,mix([222,200,160],[252,240,212],n),0.55+0.35*pos+(n-0.5)*0.25)
    m=(r>=1.958)&(r<2.025); put(m,np.broadcast_to(np.array([140,130,115])/255,r.shape+(3,)),0.04+0.05*n)
    m=(r>=2.025)&(r<2.27); put(m,mix([215,195,155],[240,226,196],n),0.6+(n-0.5)*0.3)
    m=(r>2.214)&(r<2.219); a[m]=0.02
    m=(r>=2.32)&(r<2.335); col[m]=1.0; a[m]=0.35
    # boost opacity a bit for icon legibility
    return col, np.clip(a*1.25,0,1)

def planet_layer(N, R, cx=0.0, cy=0.0, elev=np.radians(24), roll=np.radians(-16)):
    """RGBA float image of ringed planet; coords u,v in [-1,1], v down."""
    ys,xs=np.mgrid[0:N,0:N]
    u=(xs+0.5)/N*2-1-cx; v=(ys+0.5)/N*2-1-cy
    cr,sr=np.cos(roll),np.sin(roll)
    x=(u*cr+v*sr)/R; y=(-u*sr+v*cr)/R   # planet units, y down
    se,ce=np.sin(elev),np.cos(elev)
    L=np.array([-0.62,-0.40,0.67]); L/=np.linalg.norm(L)
    # rotate light into rolled frame
    L=np.array([L[0]*cr+L[1]*sr, -L[0]*sr+L[1]*cr, L[2]])
    up=np.array([0,-ce,se])   # planet axis in view frame (y down)
    # ring plane basis: points P = a*X + b*Zr, X=(1,0,0), Zr=(0,se,ce) (toward viewer, appears lower)
    Zr=np.array([0,se,ce])
    rgb=np.zeros((N,N,3)); A=np.zeros((N,N))
    # ring at pixel: x=a, y=b*se -> b=y/se ; depth=b*ce
    a_=x; b_=y/se; rr=np.hypot(a_,b_)
    rcol,ralpha=ring_rgba(rr)
    P=np.stack([a_, y, b_*ce],-1)
    # planet shadow on ring: ray P + t L hits unit sphere with t>0
    bq=(P*L).sum(-1); cq=(P*P).sum(-1)-1; disc=bq*bq-cq
    shadow=(disc>0)&(-bq+np.sqrt(np.maximum(disc,0))>0)&(-bq>0)
    lit_side=np.sign(np.dot(L,np.cross(np.array([1,0,0]),Zr)))  # which face of ring is lit
    rshade=np.where(shadow,0.12,1.0)*(0.85 if lit_side>0 else 0.55)
    ring_rgb=rcol*rshade[...,None]
    front=b_>0
    # sphere
    d2=x*x+y*y; ins=d2<1; z=np.sqrt(np.clip(1-d2,0,1))
    n=np.stack([x,y,z],-1)
    lat=(n*up).sum(-1)
    lon_noise=noise1((lat+1)*0.5,5,60)
    band=0.5+0.5*np.sin(lat*11+np.sin(lat*4)*1.2)*0.42+ (lon_noise-0.5)*0.35
    c0,c1,c2=hexc('#fff6dc'),hexc('#e3c98f'),hexc('#6a5326')
    band=np.clip(band,0,1)[...,None]
    base=np.where(band>0.5, c1+(c0-c1)*(band-0.5)*2, c2+(c1-c2)*band*2*0.85+ (c1-c2)*0.15)
    polar=np.clip((np.abs(lat)-0.75)/0.25,0,1)[...,None]
    base=base*(1-0.35*polar)+np.array([0.55,0.6,0.62])*0.35*polar*base.mean(-1,keepdims=True)*1.6
    ndl=(n*L).sum(-1)
    diff=np.clip(ndl*1.1+0.08,0,1)**0.9
    diff=diff*(0.55+0.45*z**0.35)  # limb darkening
    # ring shadow on planet: ray from surface along L intersect ring plane (normal N_r)
    Nr=np.cross(np.array([1.,0,0]),Zr)
    denom=np.dot(L,Nr); t=-(n*Nr).sum(-1)/denom
    H=n+t[...,None]*L
    hr=np.hypot(H[...,0], (H*Zr).sum(-1))
    _,ha=ring_rgba(hr)
    rs=np.where(t>0,1-0.75*ha,1)
    prgb=base*(diff*rs)[...,None]
    prgb+= np.array([1,0.85,0.6])*0.06*np.clip(1-z,0,1)[...,None]**3*ins[...,None]  # faint rim
    # composite: back ring, planet, front ring
    back=(~front)&(~ins)
    rgb=ring_rgb*ralpha[...,None]*back[...,None]; A=ralpha*back
    rgb=np.where(ins[...,None],prgb,rgb); A=np.where(ins,1.0,A)
    fa=ralpha*front
    rgb=rgb*(1-fa[...,None])+ring_rgb*fa[...,None]; A=A+fa*(1-A)
    # premultiplied -> straight
    straight=np.where(A[...,None]>1e-4, rgb/np.maximum(A,1e-4)[...,None],0)
    return np.dstack([np.clip(straight,0,1),np.clip(A,0,1)])

def background(N, stars=True):
    ys,xs=np.mgrid[0:N,0:N]
    u=(xs+0.5)/N*2-1; v=(ys+0.5)/N*2-1
    d=np.hypot(u+0.15,v+0.2)
    glow=np.exp(-d*d*1.6)
    c=SPACE+ (hexc('#111a3c')-SPACE)*glow[...,None]*0.8 + (hexc('#3a2a52')-SPACE)*np.exp(-np.hypot(u-0.55,v-0.6)**2*4)[...,None]*0.35
    img=Image.fromarray((np.clip(c,0,1)*255).astype(np.uint8))
    if stars:
        dr=ImageDraw.Draw(img); r2=np.random.default_rng(11)
        for _ in range(int(N*N/5200)):
            x,y=r2.random(2)*N; b=r2.random()**3
            s=max(0.6,N/1024*(0.8+2.2*b)); col=tuple(int(k) for k in (np.array([210,220,255])*(0.35+0.65*b)))
            dr.ellipse([x-s,y-s,x+s,y+s],fill=col)
        img=img.filter(ImageFilter.GaussianBlur(N/1024*0.6))
    return img.convert('RGBA')

def to_img(rgba, size):
    im=Image.fromarray((rgba*255).round().astype(np.uint8),'RGBA')
    return im.resize((size,size),Image.LANCZOS)

SS=2048
# adaptive foreground (safe zone radius 0.611 of half-width)
fg=to_img(planet_layer(SS,0.25),1024); fg.save(OUT+'adaptive-foreground-1024.png')
bg=background(1024); bg.convert('RGB').save(OUT+'adaptive-background-1024.png')
# full-bleed composite used for Play icon and legacy launcher
big=to_img(planet_layer(SS,0.335),1024)
comp=Image.alpha_composite(bg,big)
comp.convert('RGB').resize((512,512),Image.LANCZOS).save(OUT+'play-icon-512.png')
comp.convert('RGB').save(OUT+'icon-composite-1024.png')
# preview of adaptive in circle mask
prev=Image.alpha_composite(bg,fg); m=Image.new('L',(1024,1024),0); ImageDraw.Draw(m).ellipse([1024*(1-0.611*1.18)/2]*2+[1024-1024*(1-0.611*1.18)/2]*2,fill=255)
p2=Image.new('RGBA',(1024,1024),(0,0,0,0)); p2.paste(prev,(0,0),m); p2.save(OUT+'adaptive-preview-circle.png')
# mipmaps
dens={'mdpi':1,'hdpi':1.5,'xhdpi':2,'xxhdpi':3,'xxxhdpi':4}
comp_round=Image.alpha_composite(background(1024),to_img(planet_layer(SS,0.37),1024))
for k,s in dens.items():
    d=OUT+'mipmap/mipmap-'+k+'/'; import os; os.makedirs(d,exist_ok=True)
    L=int(48*s); F=int(108*s)
    fg.resize((F,F),Image.LANCZOS).save(d+'ic_launcher_foreground.png')
    # legacy square: rounded square, small transparent margin (Android legacy grid ~ 44/48)
    sq=comp.resize((L*4,L*4),Image.LANCZOS); m=Image.new('L',sq.size,0)
    pad=int(L*4*2/48); ImageDraw.Draw(m).rounded_rectangle([pad,pad,L*4-pad,L*4-pad],radius=int(L*4*0.18),fill=255)
    o=Image.new('RGBA',sq.size,(0,0,0,0)); o.paste(sq,(0,0),m); o.resize((L,L),Image.LANCZOS).save(d+'ic_launcher.png')
    rd=comp_round.resize((L*4,L*4),Image.LANCZOS); m=Image.new('L',rd.size,0)
    ImageDraw.Draw(m).ellipse([pad,pad,L*4-pad,L*4-pad],fill=255)
    o=Image.new('RGBA',rd.size,(0,0,0,0)); o.paste(rd,(0,0),m); o.resize((L,L),Image.LANCZOS).save(d+'ic_launcher_round.png')
print('ok')
for k,s in dens.items():
    F=int(108*s); bg.resize((F,F),Image.LANCZOS).convert('RGB').save(OUT+'mipmap/mipmap-'+k+'/ic_launcher_background.png')
d=OUT+'mipmap/mipmap-anydpi-v26/'; os.makedirs(d,exist_ok=True)
x='''<?xml version="1.0" encoding="utf-8"?>
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
    <background android:drawable="@mipmap/ic_launcher_background"/>
    <foreground android:drawable="@mipmap/ic_launcher_foreground"/>
    <monochrome android:drawable="@mipmap/ic_launcher_foreground"/>
</adaptive-icon>
'''
for n in ('ic_launcher.xml','ic_launcher_round.xml'): open(d+n,'w').write(x)
