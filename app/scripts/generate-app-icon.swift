#!/usr/bin/env swift
// Remy icon assets. Run from any working directory:
//   swift app/scripts/generate-app-icon.swift
//   swift app/scripts/generate-app-icon.swift --menu-only
// Full generation resamples Artwork/remy-app-icon.png into the existing
// AppIcon catalog slots. It never changes the approved raster master.
// The menu-bar template is original code-native geometry, designed at 18pt.
import AppKit
import CoreGraphics
import ImageIO

struct IconError: Error, CustomStringConvertible {
    let description: String
    init(_ description: String) { self.description = description }
}

let scriptURL = URL(fileURLWithPath: CommandLine.arguments[0]).resolvingSymlinksInPath()
let appRoot = scriptURL.deletingLastPathComponent().deletingLastPathComponent()
let assets = appRoot.appendingPathComponent("Dictate Anywhere/Assets.xcassets")
var masterURL = appRoot.appendingPathComponent("Artwork/remy-app-icon.png")
var menuOnly = false
var args = Array(CommandLine.arguments.dropFirst())
while !args.isEmpty {
    switch args.removeFirst() {
    case "--menu-only": menuOnly = true
    case "--master":
        guard !args.isEmpty else { throw IconError("--master requires a PNG path") }
        masterURL = URL(fileURLWithPath: args.removeFirst())
    case "--help", "-h":
        print("Usage: swift generate-app-icon.swift [--menu-only] [--master /path/to/square-RGBA.png]")
        exit(0)
    default: throw IconError("Unknown argument; use --help")
    }
}

func context(size: Int) throws -> CGContext {
    guard let context = CGContext(data: nil, width: size, height: size,
        bitsPerComponent: 8, bytesPerRow: 0,
        space: CGColorSpace(name: CGColorSpace.sRGB)!,
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
    else { throw IconError("Unable to create \(size)px RGBA context") }
    return context
}

func writePNG(_ image: CGImage, to url: URL, pointSize: CGFloat) throws {
    let rep = NSBitmapImageRep(cgImage: image)
    rep.size = NSSize(width: pointSize, height: pointSize)
    guard let data = rep.representation(using: .png, properties: [:])
    else { throw IconError("Unable to encode \(url.lastPathComponent)") }
    try data.write(to: url, options: .atomic)
    print("wrote \(url.lastPathComponent) (\(image.width)px)")
}

func drawMenuMark(scale: Int) throws -> CGImage {
    let ctx = try context(size: 18 * scale)
    // Top-left origin. Geometry stays on whole/half points at every scale.
    ctx.translateBy(x: 0, y: CGFloat(18 * scale))
    ctx.scaleBy(x: CGFloat(scale), y: -CGFloat(scale))
    ctx.setFillColor(CGColor(gray: 0, alpha: 1))
    func rounded(_ rect: CGRect, _ radius: CGFloat) {
        ctx.addPath(CGPath(roundedRect: rect, cornerWidth: radius, cornerHeight: radius, transform: nil))
        ctx.fillPath()
    }
    // Rounded ceramic head and small earpieces form a clear robot silhouette.
    rounded(CGRect(x: 2, y: 2, width: 14, height: 11), 4)
    rounded(CGRect(x: 0.5, y: 5.5, width: 2, height: 5), 1)
    rounded(CGRect(x: 15.5, y: 5.5, width: 2, height: 5), 1)
    // Transparent visor leaves two friendly eyes as solid template ink.
    ctx.setBlendMode(.clear)
    rounded(CGRect(x: 4, y: 5, width: 10, height: 5), 2)
    ctx.setBlendMode(.normal)
    ctx.setStrokeColor(CGColor(gray: 0, alpha: 1))
    ctx.setLineWidth(1.2)
    ctx.setLineCap(.round)
    for x: CGFloat in [6.5, 11.5] {
        ctx.move(to: CGPoint(x: x - 0.7, y: 7.9))
        ctx.addQuadCurve(to: CGPoint(x: x + 0.7, y: 7.9), control: CGPoint(x: x, y: 6.4))
        ctx.strokePath()
    }
    // A small bowtie distinguishes the waiter from a generic robot.
    for direction: CGFloat in [-1, 1] {
        ctx.move(to: CGPoint(x: 9, y: 15.2))
        ctx.addLine(to: CGPoint(x: 9 + direction * 3.5, y: 13.8))
        ctx.addLine(to: CGPoint(x: 9 + direction * 3.5, y: 17))
        ctx.closePath()
        ctx.fillPath()
    }
    rounded(CGRect(x: 8, y: 14.4, width: 2, height: 2), 0.7)
    guard let image = ctx.makeImage() else { throw IconError("Unable to render menu mark") }
    return image
}

struct IconCatalog: Decodable {
    struct Slot: Decodable { let filename: String?; let size: String?; let scale: String? }
    let images: [Slot]
}

// Validate the master and complete catalog before writing any full icon output.
var appOutputs: [(URL, Int)] = []
var master: CGImage?
if !menuOnly {
    guard let source = CGImageSourceCreateWithURL(masterURL as CFURL, nil),
          let image = CGImageSourceCreateImageAtIndex(source, 0, nil)
    else { throw IconError("Cannot decode approved master: \(masterURL.path)") }
    guard image.width == image.height, image.width >= 1024,
          [.first, .last, .premultipliedFirst, .premultipliedLast].contains(image.alphaInfo)
    else { throw IconError("Master must be square, at least 1024px, and have an alpha channel") }
    let iconSet = assets.appendingPathComponent("AppIcon.appiconset")
    let catalog = try JSONDecoder().decode(IconCatalog.self, from: Data(contentsOf: iconSet.appendingPathComponent("Contents.json")))
    for slot in catalog.images {
        guard let filename = slot.filename, !filename.contains("/"), filename.hasSuffix(".png"),
              let size = slot.size?.split(separator: "x"), size.count == 2,
              size[0] == size[1], let points = Int(size[0]),
              let scaleText = slot.scale, scaleText.hasSuffix("x"),
              let scale = Int(scaleText.dropLast()), points > 0, scale > 0
        else { throw IconError("Invalid existing AppIcon slot") }
        appOutputs.append((iconSet.appendingPathComponent(filename), points * scale))
    }
    guard !appOutputs.isEmpty else { throw IconError("AppIcon catalog is empty") }
    master = image
}

let menuSet = assets.appendingPathComponent("MenuBarIcon.imageset")
for scale in 1...3 {
    try writePNG(drawMenuMark(scale: scale),
        to: menuSet.appendingPathComponent("\(18 * scale)x\(18 * scale).png"), pointSize: 18)
}
if let master {
    for (url, size) in appOutputs {
        let ctx = try context(size: size)
        ctx.interpolationQuality = .high
        ctx.draw(master, in: CGRect(x: 0, y: 0, width: size, height: size))
        guard let image = ctx.makeImage() else { throw IconError("Unable to resample master") }
        try writePNG(image, to: url, pointSize: CGFloat(size))
    }
}
print(menuOnly ? "done — robot waiter template only; AppIcon unchanged" : "done — approved raster master resampled; robot waiter template generated")
