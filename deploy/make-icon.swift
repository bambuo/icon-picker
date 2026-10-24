// 生成应用图标：深色圆角底 + 2×2 候选图标网格
// 用法：swiftc -O deploy/make-icon.swift -o /tmp/make-icon && /tmp/make-icon <输出.iconset>
import CoreGraphics
import ImageIO
import Foundation

func makeImage(size: Int) -> CGImage {
    let cs = CGColorSpace(name: CGColorSpace.sRGB)!
    let ctx = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: 0,
                        space: cs, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    let s = CGFloat(size)
    ctx.setAllowsAntialiasing(true)
    ctx.interpolationQuality = .high

    let inset = s * 0.06
    let bg = CGRect(x: inset, y: inset, width: s - inset * 2, height: s - inset * 2)
    let bgPath = CGPath(roundedRect: bg, cornerWidth: bg.width * 0.225, cornerHeight: bg.width * 0.225, transform: nil)

    ctx.saveGState()
    ctx.addPath(bgPath)
    ctx.clip()
    let bgGrad = CGGradient(colorsSpace: cs, colors: [
        CGColor(srgbRed: 0.16, green: 0.20, blue: 0.34, alpha: 1),
        CGColor(srgbRed: 0.05, green: 0.07, blue: 0.14, alpha: 1)
    ] as CFArray, locations: [0, 1])!
    ctx.drawLinearGradient(bgGrad, start: CGPoint(x: bg.minX, y: bg.maxY),
                           end: CGPoint(x: bg.maxX, y: bg.minY), options: [])

    let gap = s * 0.045
    let pad = s * 0.165
    let tile = (bg.width - pad * 2 - gap) / 2
    let palette: [CGColor] = [
        CGColor(srgbRed: 0.24, green: 0.84, blue: 0.62, alpha: 1),
        CGColor(srgbRed: 0.98, green: 0.76, blue: 0.20, alpha: 1),
        CGColor(srgbRed: 0.38, green: 0.66, blue: 0.99, alpha: 1),
        CGColor(srgbRed: 0.96, green: 0.46, blue: 0.72, alpha: 1)
    ]
    var idx = 0
    for row in 0..<2 {
        for col in 0..<2 {
            let x = bg.minX + pad + CGFloat(col) * (tile + gap)
            let y = bg.minY + pad + CGFloat(row) * (tile + gap)
            let rect = CGRect(x: x, y: y, width: tile, height: tile)
            let p = CGPath(roundedRect: rect, cornerWidth: tile * 0.24, cornerHeight: tile * 0.24, transform: nil)
            ctx.saveGState()
            ctx.addPath(p)
            ctx.clip()
            let base = palette[idx]
            idx += 1
            let comps = base.components ?? [0.5, 0.5, 0.5, 1]
            let light = CGColor(srgbRed: min(comps[0] + 0.14, 1), green: min(comps[1] + 0.14, 1),
                                blue: min(comps[2] + 0.14, 1), alpha: 1)
            let dark = CGColor(srgbRed: comps[0] * 0.78, green: comps[1] * 0.78, blue: comps[2] * 0.78, alpha: 1)
            let g = CGGradient(colorsSpace: cs, colors: [light, dark] as CFArray, locations: [0, 1])!
            ctx.drawLinearGradient(g, start: CGPoint(x: rect.minX, y: rect.maxY),
                                   end: CGPoint(x: rect.maxX, y: rect.minY), options: [])
            ctx.setFillColor(CGColor(srgbRed: 1, green: 1, blue: 1, alpha: 0.12))
            ctx.fill(CGRect(x: rect.minX, y: rect.midY, width: tile, height: tile * 0.5))
            ctx.restoreGState()
        }
    }
    ctx.restoreGState()

    ctx.addPath(bgPath)
    ctx.setStrokeColor(CGColor(srgbRed: 1, green: 1, blue: 1, alpha: 0.10))
    ctx.setLineWidth(s * 0.006)
    ctx.strokePath()

    return ctx.makeImage()!
}

func writePNG(_ image: CGImage, to path: String) {
    let url = URL(fileURLWithPath: path) as CFURL
    let dest = CGImageDestinationCreateWithURL(url, "public.png" as CFString, 1, nil)!
    CGImageDestinationAddImage(dest, image, nil)
    CGImageDestinationFinalize(dest)
}

let outDir = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "AppIcon.iconset"
try FileManager.default.createDirectory(atPath: outDir, withIntermediateDirectories: true)

let variants: [(name: String, px: Int)] = [
    ("icon_16x16.png", 16), ("icon_16x16@2x.png", 32),
    ("icon_32x32.png", 32), ("icon_32x32@2x.png", 64),
    ("icon_128x128.png", 128), ("icon_128x128@2x.png", 256),
    ("icon_256x256.png", 256), ("icon_256x256@2x.png", 512),
    ("icon_512x512.png", 512), ("icon_512x512@2x.png", 1024)
]
for v in variants {
    writePNG(makeImage(size: v.px), to: (outDir as NSString).appendingPathComponent(v.name))
}
print("iconset 完成：\(outDir)")
