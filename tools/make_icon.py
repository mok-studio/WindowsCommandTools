"""生成 CMD 工具箱的应用图标 ui/app.ico（扁平化风格）。
用 DSH 自带的 Python + Pillow，先在高分辨率画布上绘制再降采样，保证边缘平滑。
"""
import os
from PIL import Image, ImageDraw

ROOT = r"D:\minecraft\Windows_Tools\_AI\DeepSeekHarness\WorkSpace\cmdtools"
S = 1024  # 绘制画布

def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))

TOP = (79, 130, 255)     # #4F82FF
BOTTOM = (44, 92, 224)   # #2C5CE0

img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

# 1) 圆角方形底板 + 轻微垂直渐变（扁平化：只用非常克制的渐变表达层次）
grad = Image.new("RGBA", (S, S), (0, 0, 0, 0))
gd = ImageDraw.Draw(grad)
for y in range(S):
    gd.line([(0, y), (S, y)], fill=lerp(TOP, BOTTOM, y / float(S - 1)) + (255,))

mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * 0.22), fill=255)

img.paste(grad, (0, 0), mask)

d = ImageDraw.Draw(img)

# 2) 终端提示符 >
chev_w = int(S * 0.085)
pts = [(int(S * 0.30), int(S * 0.315)),
       (int(S * 0.50), int(S * 0.50)),
       (int(S * 0.30), int(S * 0.685))]
d.line(pts, fill=(255, 255, 255, 255), width=chev_w, joint="curve")
for p in (pts[0], pts[2]):
    r = chev_w // 2
    d.ellipse([p[0] - r, p[1] - r, p[0] + r, p[1] + r], fill=(255, 255, 255, 255))

# 3) 光标下划线
bar = [int(S * 0.575), int(S * 0.615), int(S * 0.735), int(S * 0.685)]
d.rounded_rectangle(bar, radius=int(S * 0.018), fill=(255, 255, 255, 255))

# 4) 降采样到 256 主图，再导出多尺寸 ico
master = img.resize((256, 256), Image.LANCZOS)

out = os.path.join(ROOT, "ui", "app.ico")
master.save(out, format="ICO",
            sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)])

# 同时留一张 PNG 便于预览
master.save(os.path.join(ROOT, "ui", "app-preview.png"), format="PNG")
print("图标已生成:", out, os.path.getsize(out), "字节")
