import json, uuid
H=7.318186780234124; PT=72
def X(x): return round(x*PT,2)
def Y(y): return round((H-y)*PT,2)
def B(cx,cy,w,h): return {"x":X(cx-w/2),"y":Y(cy+h/2),"width":round(w*PT,2),"height":round(h*PT,2)}
def LTRB(l,t,r,b): return {"x":X(l),"y":Y(t),"width":round((r-l)*PT,2),"height":round((t-b)*PT,2)}
import sys; P=str(uuid.uuid4())
ops=[{"op":"addPage","id":P,"name":"PSGL Dataflow","widthPt":round(12.66177980151704*PT,2),"heightPt":round(H*PT,2)},
     {"op":"setPage","pageId":P,"patch":{"background":{"kind":"solid","colour":"#FFFFFF","opacity":1},"grid":{"visible":False,"snap":False}}}]
ids={}
def paint(c,o=1): return {"kind":"none"} if c is None else {"kind":"solid","colour":c,"opacity":o}
def txt(v,size=14,colour="#262626",bold=False,h="center",v_="middle",pad=2,wrap=True):
    return {"value":v,"fontFamily":"Calibri","fontSizePt":size,"bold":bold,"italic":False,"underline":False,"colour":colour,"horizontalAlign":h,"verticalAlign":v_,"wrap":wrap,"paddingPt":pad}
def shape(alias,bounds,fill,stroke,sw=1.25,preset="rectangle",r=None,text=None,svg=None,rot=0,dash="solid"):
    i=str(uuid.uuid4()); ids[alias]=i
    g={"svgPath":svg} if svg else {"preset":preset}
    if r is not None and not svg: g["cornerRadiusPt"]=r
    o={"op":"createShape","id":i,"alias":alias,"pageId":P,"bounds":bounds,"geometry":g,
       "style":{"fill":paint(fill),"stroke":paint(stroke),"strokeWidthPt":sw,"dash":dash}}
    if rot: o["rotationDeg"]=rot
    if text: o["text"]=text
    ops.append(o); return i
def label(alias,bounds,text,rot=0):
    i=str(uuid.uuid4()); ids[alias]=i
    o={"op":"createText","id":i,"alias":alias,"pageId":P,"bounds":bounds,"text":text}
    if rot: o["rotationDeg"]=rot
    ops.append(o)
def line(alias,pts,colour,w,dash="solid"):
    ops.append({"op":"createConnector","alias":alias,"pageId":P,"from":{"point":{"x":X(pts[0][0]),"y":Y(pts[0][1])},"glue":"none"},"to":{"point":{"x":X(pts[1][0]),"y":Y(pts[1][1])},"glue":"none"},"route":"straight","waypoints":[],"style":{"colour":colour,"widthPt":w,"dash":dash,"startArrow":"none","endArrow":"none"}})
def conn(alias,a,pa,b,pb,route="orthogonal",wp=None,start="none"):
    o={"op":"createConnector","alias":alias,"pageId":P,"from":{"elementId":ids[a],"port":pa,"glue":"static"},"to":{"elementId":ids[b],"port":pb,"glue":"static"},"route":route,"waypoints":[{"x":X(x),"y":Y(y)} for x,y in (wp or [])],"style":{"colour":"#7f7f7f","widthPt":2.25,"dash":"solid","startArrow":start,"endArrow":"block","endArrowSize":"small"}}
    ops.append(o)

L,R,BOT,TOP=0.411,12.106,0.411,6.907
TITLE=0.39; HDR=0.197; LANE=1.1811; STRIP=0.511
shape("frame",LTRB(L,TOP,R,BOT),"#FFFFFF","#7f7f7f",1)
shape("title",LTRB(L,TOP,R,TOP-TITLE),"#5b9ad4","#4472c4",1,text=txt("PSGL Dataflow",14,"#FFFFFF",True,"left",pad=6))
hdr_t=TOP-TITLE; hdr_b=hdr_t-HDR
cols=[(L,0.922,""),(0.922,2.714,"Day 0"),(2.714,5.807,"Day 1"),(5.807,R,"Day 2")]
for i,(a,b,t) in enumerate(cols):
    if t: shape("hdr-"+t,LTRB(a,hdr_t,b,hdr_b),"#f6f6f6","#7f7f7f",0.75,text=txt(t,10,"#262626",False,"right",pad=6))
lanes=[("EDW","#ffd47a","#bf9000","#262626"),("Corona / Sensical","#a9d18e","#548235","#262626"),("Power Platform","#8faadc","#2f5597","#262626"),("LEGO (Azure)","#f4b183","#b4735a","#262626"),("Macquarie","#a5a5a5","#9c6f5e","#FFFFFF")]
for i,(n,fill,edge,tc) in enumerate(lanes):
    top=hdr_b-i*LANE; bot=top-LANE
    shape("lane-"+n,LTRB(L,top,R,bot),"#FFFFFF",edge,1.5)
    shape("strip-"+n,LTRB(L,top,L+STRIP,bot),fill,edge,1.5)
    # vertical lane title: a rotated text box whose pre-rotation frame is LANE wide, STRIP tall
    cx,cy=L+STRIP/2,(top+bot)/2
    label("lanetxt-"+n,B(cx,cy,LANE-0.1,STRIP),txt(n,10,tc,False),rot=-90)
for x in (0.922,2.714,5.807):
    line(f"vline-{x}",[(x,hdr_t),(x,BOT)],"#7f7f7f",1)
line("day2-dash",[(11.29,hdr_t),(11.29,BOT)],"#000000",0.75,"dashed")

STORED="M 8 0 L 100 0 Q 90 50 100 100 L 8 100 Q 0 50 8 0 Z"
DOC="M 0 0 L 100 0 L 100 88 Q 75 72 50 88 Q 25 104 0 88 Z M 0 18 L 100 18"
def pill(alias,cx,cy,w,h,fill,edge,t): shape(alias,B(cx,cy,w,h),fill,edge,1.25,"rounded-rectangle",r=h*PT/2,text=txt(t))
def box(alias,cx,cy,w,h,fill,edge,t): shape(alias,B(cx,cy,w,h),fill,edge,1.25,text=txt(t))
def stored(alias,cx,cy,w,h,fill,edge,t): shape(alias,B(cx,cy,w,h),fill,edge,1.25,svg=STORED,text=txt(t))
pill("ers",1.8218,5.7260,1.5748,0.9449,"#ffe08a","#d49f00","ERS Daily Power\nFile Extract")
pill("equinox",1.8218,4.5449,1.5748,0.9449,"#b5d6a0","#5c8f39","Equinox Daily\nGas File extract")
stored("vmfil-a",3.6307,4.5449,1.2425,0.9252,"#b5d6a0","#5c8f39","VM-FIL01")
box("enricher",3.6335,3.3638,1.2207,0.9449,"#a9b8e6","#375ea3","PSGL Enricher")
stored("lego-a",3.6334,2.1713,1.2601,0.9843,"#f6b99b","#c56727","Lego SFTP")
box("fcs-a",5.1181,2.1711,1.0827,0.9449,"#f6b99b","#c56727","FCS File\nLoader")
box("d365",6.6929,2.1711,1.1811,0.9449,"#f6b99b","#c56727","D365 General Ledger")
shape("report",B(7.7756,2.4385,0.5906,0.4281),"#f6b99b","#c56727",1,svg=DOC,text=txt("PSGL Report",6,pad=1))
box("fcs-b",8.8583,2.1738,1.0827,0.9449,"#f6b99b","#c56727","FCS File\nLoader")
stored("lego-b",10.3407,2.1738,1.2601,0.9843,"#f6b99b","#c56727","Lego SFTP")
box("sch03",9.4686,4.5276,1.2207,0.9449,"#b5d6a0","#5c8f39","VM-SCH03")
pill("vmfil-b",11.2205,4.5276,1.5748,0.9449,"#b5d6a0","#5c8f39","VM-FIL01")
pill("macq",11.2205,0.9843,1.5748,0.9449,"#d4d4d4","#898989","Macquarie\nSFTP")

conn("c1","ers","east","vmfil-a","north",wp=[(3.6307,5.726)])
conn("c2","equinox","east","vmfil-a","west")
conn("c3","enricher","north","vmfil-a","south")
conn("c4","enricher","south","lego-a","north")
conn("c5","fcs-a","west","lego-a","east")
conn("c6","fcs-a","east","d365","west")
conn("c7","d365","east","fcs-b","west")
conn("c8","fcs-b","east","lego-b","west")
conn("c9","sch03","west","lego-b","north",wp=[(8.67,4.5276),(8.67,3.22),(10.3407,3.22)])
conn("c10","sch03","east","vmfil-b","west")
conn("c11","sch03","south","macq","north",wp=[(9.4686,3.4),(11.2205,3.4)])
for n,(cx,cy) in enumerate([(2.874,5.9055),(2.8677,4.7539),(2.874,3.9665),(2.874,2.7854)],1):
    shape(f"num{n}",B(cx,cy,0.2559,0.2559),"#ffc000","#262626",1.25,"ellipse",text=txt(str(n),9,"#262626",True,pad=0))
json.dump(ops,open("ops.json","w"),indent=1)
print(len(ops))
