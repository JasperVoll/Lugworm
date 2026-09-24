"""Generate the three ClayToolkit component icons (professional blue set)."""
import cairosvg, os
HERE = os.path.dirname(os.path.abspath(__file__))

LINE="#E9F1FA"; LINE2="#C7DCF0"; BLUE="#3E9BDE"; BLUE_DEEP="#2266A6"
BLUE_LIGHT="#8FCBF2"; STEEL="#BFD0E2"

DEFS="""
  <defs>
    <linearGradient id="tile" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#2A3D55"/><stop offset="1" stop-color="#141E2B"/>
    </linearGradient>
    <linearGradient id="blue" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#54AEEC"/><stop offset="1" stop-color="#2A72B8"/>
    </linearGradient>
    <linearGradient id="body" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#EFF5FC"/><stop offset="1" stop-color="#C9DDF1"/>
    </linearGradient>
  </defs>
"""

def tile():
    return ('<rect x="10" y="10" width="236" height="236" rx="42" fill="url(#tile)"/>'
        '<rect x="10.5" y="10.5" width="235" height="235" rx="41.5" fill="none" '
        'stroke="#0B131C" stroke-opacity="0.55" stroke-width="1"/>'
        '<rect x="11" y="11" width="234" height="234" rx="41" fill="none" '
        'stroke="#FFFFFF" stroke-opacity="0.06" stroke-width="2"/>'
        '<rect x="20" y="20" width="216" height="216" rx="34" fill="none" '
        'stroke="'+BLUE+'" stroke-opacity="0.28" stroke-width="1.5"/>')

def svg(body):
    return ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" '
            'width="256" height="256">'+DEFS+tile()+body+'</svg>')

vase=("M104,70 C101,82 101,86 86,104 C72,120 70,144 76,168 "
      "C80,186 94,198 110,202 L146,202 C162,198 176,186 180,168 "
      "C186,144 184,120 170,104 C155,86 155,82 152,70 Z")
slicer_body=('<path d="'+vase+'" fill="url(#body)"/>'
    '<clipPath id="vclip"><path d="'+vase+'"/></clipPath>'
    '<g clip-path="url(#vclip)">'
    + ''.join('<rect x="58" y="%d" width="140" height="4" fill="%s" opacity="0.9"/>'%(y,BLUE)
              for y in (96,120,144,168,190))
    + '</g>'
    + ''.join('<line x1="54" y1="%d" x2="202" y2="%d" stroke="%s" stroke-width="4.5" '
              'stroke-linecap="round"/>'%(y,y,BLUE_DEEP) for y in (120,168))
    + '<path d="'+vase+'" fill="none" stroke="'+BLUE_DEEP+'" stroke-width="5"/>')

mover_body=('<g fill="none" stroke="'+LINE+'" stroke-width="6.5">'
    '<ellipse cx="128" cy="142" rx="66" ry="39"/>'
    '<ellipse cx="128" cy="142" rx="38" ry="22"/></g>'
    '<g stroke="'+LINE+'" stroke-width="4.5" stroke-linecap="round">'
    '<line x1="117" y1="142" x2="139" y2="142"/>'
    '<line x1="128" y1="133" x2="128" y2="151"/></g>'
    '<path d="M188,118 A70,40 0 0 1 152,87" fill="none" stroke="'+BLUE+'" '
    'stroke-width="6.5" stroke-linecap="round"/>'
    '<path d="M152,87 l21,-4 l-7,20 z" fill="'+BLUE+'"/>'
    '<circle cx="194" cy="142" r="10.5" fill="'+BLUE+'" stroke="'+BLUE_LIGHT+'" stroke-width="3"/>')

gcode_body=('<path d="M102,60 L154,60 L139,150 L128,168 L117,150 Z" fill="'+STEEL+'"/>'
    '<rect x="98" y="56" width="60" height="13" rx="5" fill="'+LINE+'"/>'
    '<rect x="103" y="80" width="50" height="7" rx="3" fill="'+BLUE_DEEP+'" opacity="0.5"/>'
    '<path d="M80,170 L176,170 L176,196 L80,196 L80,220" fill="none" '
    'stroke="url(#blue)" stroke-width="13" stroke-linecap="round" stroke-linejoin="round"/>'
    '<line x1="128" y1="168" x2="128" y2="170" stroke="'+BLUE+'" stroke-width="13" '
    'stroke-linecap="round"/>')

BUILD=150
_simlayers=''.join('<rect x="54" y="%d" width="148" height="3.4" fill="%s" opacity="0.92"/>'%(y,BLUE) for y in (159,168,177,186,195))
sim_body=(
    '<clipPath id="sclip"><path d="{v}"/></clipPath>'
    '<path d="{v}" fill="none" stroke="{bl}" stroke-width="4" stroke-dasharray="2 8" stroke-linecap="round" opacity="0.85"/>'
    '<g clip-path="url(#sclip)">'
    '<rect x="54" y="{B}" width="148" height="64" fill="url(#body)"/>'
    '{layers}'
    '</g>'
    '<clipPath id="lowclip"><rect x="40" y="{B}" width="176" height="80"/></clipPath>'
    '<g clip-path="url(#lowclip)"><path d="{v}" fill="none" stroke="{bd}" stroke-width="5"/></g>'
    '<g clip-path="url(#sclip)"><rect x="40" y="{B2}" width="176" height="5" fill="{b}"/></g>'
    '<circle cx="190" cy="182" r="26" fill="url(#blue)" stroke="{bl}" stroke-width="4"/>'
    '<path d="M181,169 L207,182 L181,195 Z" fill="#FFFFFF"/>'
).format(v=vase, bl=BLUE_LIGHT, bd=BLUE_DEEP, b=BLUE, B=BUILD, B2=BUILD-2, layers=_simlayers)

ICONS={"ClaySlicer":slicer_body,"ClayMover":mover_body,"ClayGcode":gcode_body,"ClaySim":sim_body}
for name,body in ICONS.items():
    src=svg(body)
    open(os.path.join(HERE,name+".svg"),"w",encoding="utf-8").write(src)
    for px in (256,24):
        cairosvg.svg2png(bytestring=src.encode("utf-8"),
            write_to=os.path.join(HERE,"%s_%d.png"%(name,px)),
            output_width=px,output_height=px)
    print("rendered",name)

labels=["ClaySlicer","ClayMover","ClayGcode","ClaySim"]
sheet='<svg xmlns="http://www.w3.org/2000/svg" width="1104" height="300" viewBox="0 0 1104 300">'
for i,name in enumerate(labels):
    x=16+i*272
    sheet+=('<svg x="%d" y="8" width="256" height="256" viewBox="0 0 256 256">%s%s%s</svg>'
            %(x,DEFS,tile(),ICONS[name]))
    sheet+=('<text x="%d" y="288" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" '
            'font-size="22" fill="#5B6B7C">%s</text>'%(x+128,name))
sheet+='</svg>'
cairosvg.svg2png(bytestring=sheet.encode("utf-8"),
    write_to=os.path.join(HERE,"_contact_sheet.png"),output_width=1104,output_height=300)
print("rendered contact sheet")
