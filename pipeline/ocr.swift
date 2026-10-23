import Foundation
import Vision
import AppKit

func jsonEscape(_ s: String) -> String {
    var out = ""
    for u in s.unicodeScalars {
        switch u {
        case "\"": out += "\\\""
        case "\\": out += "\\\\"
        case "\n": out += "\\n"
        case "\r": out += "\\r"
        case "\t": out += "\\t"
        default:
            if u.value < 0x20 { out += String(format: "\\u%04x", u.value) } else { out.unicodeScalars.append(u) }
        }
    }
    return out
}

let args = CommandLine.arguments
guard args.count >= 2 else {
    FileHandle.standardError.write("usage: ocr -list <file> | ocr <image...>\n".data(using: .utf8)!)
    exit(2)
}

var paths: [String] = []
if args[1] == "-list", args.count >= 3 {
    let body = (try? String(contentsOfFile: args[2], encoding: .utf8)) ?? ""
    paths = body.split(separator: "\n").map(String.init).filter { !$0.isEmpty }
} else {
    paths = Array(args.dropFirst())
}

for p in paths {
    autoreleasepool {
        guard let img = NSImage(contentsOfFile: p),
              let cg = img.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
            print("{\"file\":\"\(jsonEscape(p))\",\"error\":\"load\"}")
            return
        }
        let req = VNRecognizeTextRequest()
        req.recognitionLevel = .accurate
        req.usesLanguageCorrection = true
        req.recognitionLanguages = ["en-US", "pt-BR", "zh-Hans"]
        let handler = VNImageRequestHandler(cgImage: cg, options: [:])
        do {
            try handler.perform([req])
            let obs = (req.results ?? []).sorted { $0.boundingBox.midY > $1.boundingBox.midY }
            var parts: [String] = []
            for o in obs {
                let minConf = Float(ProcessInfo.processInfo.environment["OCR_MIN_CONF"] ?? "0.15") ?? 0.15
                if let c = o.topCandidates(1).first, c.confidence > minConf {
                    parts.append(c.string)
                }
            }
            let text = parts.joined(separator: " | ")
            print("{\"file\":\"\(jsonEscape(p))\",\"text\":\"\(jsonEscape(text))\"}")
        } catch {
            print("{\"file\":\"\(jsonEscape(p))\",\"error\":\"ocr\"}")
        }
    }
}
