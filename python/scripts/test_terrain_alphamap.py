"""真实 Unity 联调:在 Terrain 上绘制 6 层纹理渐变条,验证 terrain.set_alphamaps。"""
import math
import sys

sys.path.insert(0, r"D:/Files/unityprojects/temptest/Assets/unity-python-bridge/python")
from unity_bridge import UnityClient

W, H = 64, 64          # 绘制区域 (alphamap 像素)
LAYERS = 6             # 与 terrain-get-layers 返回一致
X_BASE = 200           # 绘制在 (200,200) 起的区域,避开全图
Z_BASE = 200

# 构造渐变:沿 x 方向 6 层依次主导,每像素权重和 = 1
data = []
for y in range(H):
    for x in range(W):
        t = x / (W - 1)                      # 0..1
        weights = [0.0] * LAYERS
        # 每层在 t 的 [i/6, (i+1)/6] 区间主导,区间内平滑过渡
        for i in range(LAYERS):
            center = i / (LAYERS - 1) if LAYERS > 1 else 0.5
            weights[i] = math.exp(-((t - center) ** 2) * 80.0)   # 高斯渐变
        s = sum(weights)
        if s <= 0:
            weights[0] = 1.0
            s = 1.0
        data.extend([w / s for w in weights])

print(f"绘制区域: xBase={X_BASE} zBase={Z_BASE} {W}x{H}, 层数={LAYERS}, data长度={len(data)}")
assert len(data) == W * H * LAYERS, f"数据长度错误: {len(data)} != {W*H*LAYERS}"

with UnityClient(timeout=10) as c:
    r = c.set_alphamaps(terrain="Terrain", x_base=X_BASE, z_base=Z_BASE,
                        width=W, height=H, data=data)
    print("set_alphamaps 返回:", r)

    # 读回验证
    g = c.get_alphamaps(terrain="Terrain", x_base=X_BASE, z_base=Z_BASE,
                        width=W, height=H)
    d = g["data"]
    print(f"读回: {g['width']}x{g['height']} layers={g['layers']} count={g['count']}")

    # 检查每像素权重和是否=1,以及渐变是否生效
    ok = True
    for i in range(0, len(d), LAYERS):
        s = sum(d[i:i + LAYERS])
        if abs(s - 1.0) > 0.01:
            ok = False
            print(f"  权重和异常 @像素{i//LAYERS}: {s:.4f}")
    print("每像素权重和=1 校验:", "通过" if ok else "失败")

    # 打印渐变首尾几行的层分布(看渐变是否真的画上了)
    print("渐变抽样(每行第1/16/32/48/63 列的权重):")
    for row in (0, 16, 32, 48, 63):
        line = []
        for col in (0, 16, 32, 48, 63):
            idx = (row * W + col) * LAYERS
            dom = max(range(LAYERS), key=lambda i: d[idx + i])
            line.append(f"x={col}:层{dom}")
        print(f"  y={row:>2}: " + "  ".join(line))
