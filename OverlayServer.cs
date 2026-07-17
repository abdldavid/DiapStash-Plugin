using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DiapStash_Plugin
{
    public class OverlayServer
    {
        private static OverlayServer? _instance;
        public static OverlayServer Instance => _instance ??= new OverlayServer();
        private HttpListener? _listener;
        private bool _isRunning = false;

        // Design properties
        public double CardW { get; set; } = 800; public double CardH { get; set; } = 200;
        public int TransitionType { get; set; } = 0; public double TransitionDurationMs { get; set; } = 400;
        public double StayOnScreenDurationMs { get; set; } = 5000;
        public double CardCornerRadius { get; set; } = 12;
        public string CardBackgroundHex { get; set; } = "#FFFFFF";
        public bool ForcePreviewTrigger { get; set; } = false;
        public bool IsEditing { get; set; } = false;

        // Live Data (Updated periodically by StreamingPage)
        public string LiveProductName { get; set; } = "Product Name";
        public string LiveSize { get; set; } = "M";
        public int LiveWetPercentage { get; set; } = 50;
        public int LiveMessPercentage { get; set; } = 20;
        public string LiveStatusMessage { get; set; } = "TELEMETRY";
        public string LiveElapsed { get; set; } = "00:00:00";
        public string LiveImageUrl { get; set; } = "";

        // Dynamic Elements
        public List<OverlayElement> Elements { get; set; } = new List<OverlayElement>();

        public void Start()
        {
            if (_isRunning) return; _listener = new HttpListener(); _listener.Prefixes.Add("http://localhost:8890/overlay/");
            try { _listener.Start(); _isRunning = true; Task.Run(ListenLoop); } catch { }
        }

        private async Task ListenLoop()
        {
            while (_isRunning)
            {
                try
                {
                    var ctx = await _listener!.GetContextAsync(); 
                    var resp = ctx.Response; 
                    resp.Headers.Add("Access-Control-Allow-Origin", "*");
                    resp.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
                    resp.Headers.Add("Pragma", "no-cache");
                    resp.Headers.Add("Expires", "0");

                    if (ctx.Request.Url!.AbsolutePath == "/overlay/trigger")
                    {
                        ForcePreviewTrigger = true; resp.StatusCode = 200; resp.ContentLength64 = 0;
                    }
                    else if (ctx.Request.Url!.AbsolutePath == "/overlay/local")
                    {
                        try
                        {
                            string encodedPath = ctx.Request.QueryString["path"] ?? "";
                            if (!string.IsNullOrEmpty(encodedPath))
                            {
                                string path = "";
                                try { path = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedPath)); } catch { }
                                
                                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                                {
                                    byte[] img = System.IO.File.ReadAllBytes(path);
                                    string ext = System.IO.Path.GetExtension(path).ToLower();
                                    resp.ContentType = ext == ".png" ? "image/png" : (ext == ".gif" ? "image/gif" : (ext == ".webp" ? "image/webp" : "image/jpeg"));
                                    resp.ContentLength64 = img.Length;
                                    await resp.OutputStream.WriteAsync(img, 0, img.Length);
                                }
                                else { resp.StatusCode = 404; }
                            }
                            else { resp.StatusCode = 404; }
                        }
                        catch { resp.StatusCode = 500; }
                    }
                    else if (ctx.Request.Url!.AbsolutePath == "/overlay/config")
                    {
                        try
                        {
                            var state = DiapStashClient.Instance.GetCachedChangeState();
                            if (state != null)
                            {
                                LiveProductName = state.ProductName ?? "Standard Diaper Product";
                                LiveSize = state.Size ?? "N/A";
                                LiveWetPercentage = state.WetnessPercentage;
                                LiveMessPercentage = state.MessyPercentage;
                                LiveStatusMessage = state.IsActiveSession ? "Active" : "Completed";
                                if (state.StartTime != DateTime.MinValue)
                                {
                                    var diff = state.IsActiveSession ? (DateTime.Now - state.StartTime.ToLocalTime()) : (state.EndTime.Value.ToLocalTime() - state.StartTime.ToLocalTime());
                                    LiveElapsed = $"{(int)diff.TotalHours:D2}:{diff.Minutes:D2}:{diff.Seconds:D2}";
                                }
                                if (!string.IsNullOrEmpty(state.ImageUrl) && state.ImageUrl.Contains(":\\"))
                                {
                                    string encodedPath = Uri.EscapeDataString(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(state.ImageUrl)));
                                    LiveImageUrl = $"http://localhost:8890/overlay/local?path={encodedPath}&v={DateTime.Now.Ticks}";
                                }
                                else
                                {
                                    LiveImageUrl = state.ImageUrl ?? "";
                                }
                            }
                        }
                        catch { }

                        var data = new
                        {
                            cardW = CardW,
                            cardH = CardH,
                            transType = TransitionType,
                            transDur = TransitionDurationMs,
                            stayDur = StayOnScreenDurationMs,
                            cardRadius = CardCornerRadius,
                            bgHex = CardBackgroundHex,
                            
                            liveProductName = LiveProductName,
                            liveSize = LiveSize,
                            liveWet = LiveWetPercentage,
                            liveMess = LiveMessPercentage,
                            liveStatus = LiveStatusMessage,
                            liveElapsed = LiveElapsed,
                            liveImage = LiveImageUrl,
                            
                            elements = Elements,
                            
                            trigger = ForcePreviewTrigger,
                            isEditing = IsEditing
                        };
                        if (ForcePreviewTrigger) ForcePreviewTrigger = false;
                        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                        byte[] b = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, opts)); await resp.OutputStream.WriteAsync(b, 0, b.Length);
                    }
                    else { byte[] h = Encoding.UTF8.GetBytes(GetHtml()); await resp.OutputStream.WriteAsync(h, 0, h.Length); }
                    resp.Close();
                }
                catch { }
            }
        }

        private string GetHtml() => @"<!DOCTYPE html><html><head>
        <meta http-equiv='cache-control' content='no-cache, no-store, must-revalidate'>
        <meta http-equiv='pragma' content='no-cache'>
        <meta http-equiv='expires' content='0'>
        <link href='https://fonts.googleapis.com/css2?family=Outfit:wght@400;600;700&display=swap' rel='stylesheet'>
        <style>
            body { background: transparent; overflow: hidden; margin: 0; font-family: 'Outfit', 'Segoe UI', sans-serif; }
            #wrapper { position: absolute; left: 50%; top: 50%; width: var(--cardW); height: var(--cardH); transform: translate(-50%, -50%) scale(var(--scale)); display: flex; align-items: center; justify-content: center; }
            :root { --dur: 0.4s; --cardW: 800px; --cardH: 200px; --cardRadius: 12px; --scale: 1; }
            .canvas { position: absolute; left: 0; top: 0; width: 100%; height: 100%; background: rgba(18, 18, 20, 0.65); backdrop-filter: blur(16px); -webkit-backdrop-filter: blur(16px); border: 1px solid rgba(255, 255, 255, 0.1); border-radius: var(--cardRadius); box-shadow: 0 8px 32px rgba(0,0,0,0.4); overflow: hidden; color: white; transition: all var(--dur) cubic-bezier(0.25, 1, 0.5, 1); opacity: 0; }
            .canvas.trans-0 { opacity: 0; } .canvas.trans-0.active { opacity: 1; }
            .canvas.trans-1 { opacity: 0; transform: scale(0.85); } .canvas.trans-1.active { opacity: 1; transform: scale(1); }
            .canvas.trans-2 { opacity: 0; transform: translateX(100px); } .canvas.trans-2.active { opacity: 1; transform: translateX(0); }
            .canvas.trans-3 { opacity: 0; transform: translateX(-100px); } .canvas.trans-3.active { opacity: 1; transform: translateX(0); }
            .canvas.trans-4 { opacity: 0; transform: translateY(100px); } .canvas.trans-4.active { opacity: 1; transform: translateY(0); }
            .canvas.trans-5 { opacity: 0; transform: translateY(-100px); } .canvas.trans-5.active { opacity: 1; transform: translateY(0); }
            .canvas.trans-6 { opacity: 0; transform: scale(0.5); transition: all var(--dur) cubic-bezier(0.175, 0.885, 0.32, 1.275); } .canvas.trans-6.active { opacity: 1; transform: scale(1); }
        </style></head><body>
            <div id='wrapper'>
                <div id='c' class='canvas'></div>
            </div>
            <script>
                const wrapper = document.getElementById('wrapper'), c = document.getElementById('c');
                let currentTrans = -1; let isVisible = false; let hideTimeout;

                function formatCssColor(hex) {
                    if (!hex) return 'transparent';
                    hex = hex.trim();
                    if (hex.startsWith('#') && hex.length === 9) {
                        const a = hex.substring(1, 3);
                        const r = hex.substring(3, 5);
                        const g = hex.substring(5, 7);
                        const b = hex.substring(7, 9);
                        return `#${r}${g}${b}${a}`;
                    }
                    return hex;
                }

                async function sync() {
                    try {
                        const d = await (await fetch('/overlay/config?t='+Date.now())).json();
                        document.documentElement.style.setProperty('--cardW', d.cardW + 'px');
                        document.documentElement.style.setProperty('--cardH', d.cardH + 'px');
                        document.documentElement.style.setProperty('--cardRadius', d.cardRadius + 'px');
                        c.style.background = formatCssColor(d.bgHex);
                        const sX = window.innerWidth / d.cardW; const sY = window.innerHeight / d.cardH;
                        document.documentElement.style.setProperty('--scale', Math.min(sX, sY) * 0.95);
                        document.documentElement.style.setProperty('--dur', (d.transDur / 1000) + 's');
                        
                        let transChanged = false;
                        if (currentTrans !== d.transType) {
                            const oldTrans = currentTrans;
                            c.classList.remove('trans-' + currentTrans);
                            currentTrans = d.transType;
                            c.classList.add('trans-' + d.transType);
                            if (oldTrans !== -1) {
                                transChanged = true;
                            }
                        }
                        
                        c.innerHTML = ''; // Clears the canvas
                        if (d.elements) {
                            function buildElement(el, parentDom) {
                                const dom = document.createElement('div');
                                dom.style.position = 'absolute';
                                dom.style.left = (el.x !== undefined ? el.x : el.X) + 'px';
                                dom.style.top = (el.y !== undefined ? el.y : el.Y) + 'px';
                                dom.style.width = (el.width !== undefined ? el.width : el.Width) + 'px';
                                dom.style.height = (el.height !== undefined ? el.height : el.Height) + 'px';
                                dom.style.zIndex = el.zIndex !== undefined ? el.zIndex : el.ZIndex;
                                
                                const type = el.elementType || el.$type || el.ElementType;
                                if (type === 'group') {
                                    const children = el.children || el.Children || [];
                                    children.sort((a,b) => (a.zIndex !== undefined ? a.zIndex : a.ZIndex) - (b.zIndex !== undefined ? b.zIndex : b.ZIndex)).forEach(child => {
                                        buildElement(child, dom);
                                    });
                                }
                                else if (type === 'text') {
                                    const ds = el.dataSource || el.DataSource;
                                    let txt = el.customText || el.CustomText;
                                    if (ds === 'ProductName') txt = d.liveProductName;
                                    else if (ds === 'Size') txt = d.liveSize;
                                    else if (ds === 'Wetness') txt = d.liveWet + '%';
                                    else if (ds === 'Messiness') txt = d.liveMess + '%';
                                    else if (ds === 'LiveStatus') txt = d.liveStatus;
                                    else if (ds === 'ElapsedTime') {
                                        let showSec = el.showSeconds !== undefined ? el.showSeconds : (el.ShowSeconds !== undefined ? el.ShowSeconds : true);
                                        txt = showSec ? d.liveElapsed : (d.liveElapsed && d.liveElapsed.length >= 5 ? d.liveElapsed.substring(0, 5) : d.liveElapsed);
                                    }
                                    
                                    dom.style.fontFamily = el.fontFamily || el.FontFamily;
                                    dom.style.fontSize = (el.fontSize !== undefined ? el.fontSize : el.FontSize) + 'px';
                                    dom.style.fontWeight = el.fontWeight || el.FontWeight;
                                    dom.style.fontStyle = el.fontStyle || el.FontStyle || 'normal';
                                    dom.style.color = formatCssColor(el.colorHex || el.ColorHex);
                                    
                                    const align = el.textAlignment || el.TextAlignment || 'Left';
                                    
                                    const span = document.createElement('span');
                                    span.innerText = txt;
                                    span.style.width = '100%';
                                    span.style.textAlign = align.toLowerCase();
                                    dom.appendChild(span);
                                    
                                    dom.style.display = 'flex';
                                    dom.style.alignItems = 'center';
                                    dom.style.justifyContent = align === 'Center' ? 'center' : (align === 'Right' ? 'flex-end' : 'flex-start');
                                    
                                    const wrap = el.textWrap !== undefined ? el.textWrap : el.TextWrap;
                                    dom.style.whiteSpace = wrap ? 'normal' : 'nowrap';
                                    dom.style.overflow = 'hidden';
                                    dom.style.textShadow = '0 1px 2px rgba(0,0,0,0.1)';
                                } 
                                else if (type === 'bar') {
                                    const ds = el.dataSource || el.DataSource;
                                    let val = 50;
                                    if (ds === 'Wetness') val = d.liveWet;
                                    else if (ds === 'Messiness') val = d.liveMess;
                                    
                                    const cr = el.cornerRadius !== undefined ? el.cornerRadius : (el.CornerRadius !== undefined ? el.CornerRadius : 6);
                                    dom.style.background = formatCssColor(el.bgColorHex || el.BgColorHex);
                                    dom.style.borderRadius = cr + 'px';
                                    dom.style.overflow = 'hidden';
                                    dom.style.boxShadow = 'inset 0 1px 3px rgba(0,0,0,0.3)';
                                    
                                    const fill = document.createElement('div');
                                    const fillCol = el.fillColorHex || el.FillColorHex;
                                    fill.style.background = formatCssColor(fillCol);
                                    fill.style.borderRadius = cr + 'px';
                                    fill.style.boxShadow = `0 0 10px ${formatCssColor(fillCol)}`;
                                    fill.style.transition = 'width 1s ease, height 1s ease';
                                    
                                    const orient = el.orientation || el.Orientation;
                                    const widthVal = el.width !== undefined ? el.width : el.Width;
                                    const heightVal = el.height !== undefined ? el.height : el.Height;
                                    if (orient === 'Horizontal') {
                                        fill.style.height = '100%';
                                        fill.style.width = val + '%';
                                    } else {
                                        fill.style.width = '100%';
                                        fill.style.height = val + '%';
                                        fill.style.position = 'absolute';
                                        fill.style.bottom = '0';
                                    }
                                    dom.appendChild(fill);
                                }
                                else if (type === 'image') {
                                    const ds = el.dataSource || el.DataSource;
                                    let src = el.customUrl || el.CustomUrl;
                                    if (ds === 'DiapStashImage') {
                                        src = d.liveImage;
                                        if (!src) src = 'https://diapstash.com/diapstash/assets/icons/Diaper.png';
                                    }
                                    
                                    const cr = el.cornerRadius !== undefined ? el.cornerRadius : (el.CornerRadius !== undefined ? el.CornerRadius : 6);
                                    dom.style.borderRadius = cr + 'px';
                                    dom.style.border = '1px solid rgba(0,0,0,0.1)';
                                    dom.style.overflow = 'hidden';
                                    dom.style.display = 'flex';
                                    dom.style.alignItems = 'center';
                                    dom.style.justifyContent = 'center';
                                    dom.style.background = 'rgba(0,0,0,0.05)';
                                    
                                    if (src) {
                                        dom.style.backgroundImage = 'url(""' + src + '"")';
                                        dom.style.backgroundSize = el.stretch === 'Uniform' ? 'contain' : (el.stretch === 'Fill' ? '100% 100%' : 'cover');
                                        dom.style.backgroundPosition = 'center';
                                        dom.style.backgroundRepeat = 'no-repeat';
                                        if (src === 'https://diapstash.com/diapstash/assets/icons/Diaper.png') {
                                            dom.style.filter = 'opacity(0.8)';
                                        } else {
                                            dom.style.filter = 'none';
                                        }
                                    } else {
                                        dom.innerHTML = `<span style='font-size:24px'>🛒</span>`;
                                    }
                                }
                                else if (type === 'ring') {
                                    const t = el.strokeThickness !== undefined ? el.strokeThickness : (el.StrokeThickness !== undefined ? el.StrokeThickness : 10);
                                    const isFull = el.isFullCircle !== undefined ? el.isFullCircle : (el.IsFullCircle !== undefined ? el.IsFullCircle : true);
                                    const arcAng = el.arcAngle !== undefined ? el.arcAngle : (el.ArcAngle !== undefined ? el.ArcAngle : 360);
                                    const arcRot = el.arcRotation !== undefined ? el.arcRotation : (el.ArcRotation !== undefined ? el.ArcRotation : 0);
                                    const round = el.roundedCorners !== undefined ? el.roundedCorners : (el.RoundedCorners !== undefined ? el.RoundedCorners : true);
                                    const ds = el.dataSource || el.DataSource;
                                    const fillCol = el.fillColorHex || el.FillColorHex;
                                    const bgCol = el.bgColorHex || el.BgColorHex;
                                    
                                    const w = el.width !== undefined ? el.width : el.Width;
                                    const h = el.height !== undefined ? el.height : el.Height;
                                    
                                    let val = 0;
                                    if (ds === 'Messiness') val = d.liveMess;
                                    else val = d.liveWet;
                                    
                                    const pct = val / 100.0;
                                    const r = Math.max(1, Math.min(w, h) / 2.0 - t / 2.0);
                                    const cx = w / 2;
                                    const cy = h / 2;
                                    
                                    const lineCap = round ? 'round' : 'butt';
                                    const circ = 2 * Math.PI * r;
                                    const arcLen = isFull ? circ : circ * (arcAng / 360.0);
                                    const rotAng = arcRot - 90;
                                    
                                    const bgDashArr = `${arcLen} ${circ}`;
                                    const fgDashOff = circ - (arcLen * pct);
                                    
                                    let svgHTML = `<svg xmlns='http://www.w3.org/2000/svg' width='${w}' height='${h}' viewBox='0 0 ${w} ${h}'>`;
                                    svgHTML += `<circle cx='${cx}' cy='${cy}' r='${r}' fill='none' stroke='${formatCssColor(bgCol)}' stroke-width='${t}' stroke-linecap='${lineCap}' stroke-dasharray='${bgDashArr}' stroke-dashoffset='0' transform='rotate(${rotAng} ${cx} ${cy})' />`;
                                    svgHTML += `<circle cx='${cx}' cy='${cy}' r='${r}' fill='none' stroke='${formatCssColor(fillCol)}' stroke-width='${t}' stroke-linecap='${lineCap}' stroke-dasharray='${circ} ${circ}' stroke-dashoffset='${fgDashOff}' transform='rotate(${rotAng} ${cx} ${cy})' style='transition: stroke-dashoffset 1s ease;' />`;
                                    svgHTML += `</svg>`;
                                    dom.innerHTML = svgHTML;
                                }
                                parentDom.appendChild(dom);
                            }
                            d.elements.sort((a,b) => (a.zIndex !== undefined ? a.zIndex : a.ZIndex) - (b.zIndex !== undefined ? b.zIndex : b.ZIndex)).forEach(el => buildElement(el, c));
                        }
                        
                        if (d.trigger || transChanged) {
                            clearTimeout(hideTimeout);
                            c.classList.remove('active');
                            void c.offsetWidth; // Force reflow
                            c.classList.add('active');
                            isVisible = true;
                            
                            hideTimeout = setTimeout(() => {
                                c.classList.remove('active');
                                isVisible = false;
                            }, d.stayDur || 5000);
                        }
                    } catch(e) { console.error('Sync failed', e); }
                } setInterval(sync, 800);
            </script>
        </body></html>";
    }
}