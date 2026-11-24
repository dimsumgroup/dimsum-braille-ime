#!/usr/bin/env python3
"""
NVDA 字典 → macOS VoiceOver 發音表轉換工具

讀取 NVDA 的三個字典檔案：
  1. characterDescriptions.dic — 字元描述（最詳細，優先級最高）
  2. symbols.dic              — 符號發音
  3. cldr.dic                 — Unicode 字元描述

合併所有條目，輸出：
  1. DimsumPronunciations.json — JSON 格式（推薦，供 dimsum_vo_helper 載入）
  2. DimsumPronunciations.swift — Swift 字典格式（向後相容）
  3. voiceover_pronunciations.csv — CSV 中介檔

用法：
    python3 convert_to_voiceover.py [nvda_dic_dir] [output_dir]

    nvda_dic_dir: NVDA 字典目錄（預設：./nvda_dic）
    output_dir:   輸出目錄（預設：./voiceover_output）
"""

import os
import re
import sys
import csv
import json
from pathlib import Path


# ============================================================================
# 解析 characterDescriptions.dic
# ============================================================================
def parse_character_descriptions(dic_path):
    """解析 characterDescriptions.dic 格式

    格式：字元\t描述1\t描述2\t...
    例如：嘅\t(意指 的，粵語用字)\t我嘅\t係嘅\t(古時同慨嘆的慨)\t(口字邊，右面既然的既)
    """
    entries = {}
    with open(dic_path, 'r', encoding='utf-8-sig') as f:
        for line in f:
            line = line.rstrip('\n')
            if not line or line.startswith('#'):
                continue

            parts = line.split('\t')
            if len(parts) < 2:
                continue

            character = parts[0]
            descriptions = parts[1:]

            # 跳過單個 ASCII 字母 (a-z, A-Z) — TTS 引擎已能正確發音
            if len(character) == 1 and character.isascii() and character.isalpha():
                continue

            # 跳過過長的條目（可能是正則表達式）
            if len(character) > 10:
                continue

            # 合併所有描述
            replacement = ' '.join(descriptions)

            entries[character] = replacement

    return entries


# ============================================================================
# 解析 symbols.dic
# ============================================================================
def parse_symbols(dic_path):
    """解析 symbols.dic 格式

    檔案有兩個區段：
      complexSymbols: — 正則表達式，跳過
      symbols:        — 符號定義

    symbols 區段格式：identifier \t replacement \t level \t preserve
    例如：
      . sentence ending\tdot\tall\talways      ← 多詞識別符，跳過
      !\tbang\tall                              ← 單字元，保留
      \\t\t跳格                                  ← 跳脫序列，跳過
      。\t句號\tall                              ← 中文字元，保留
    """
    entries = {}
    in_symbols_section = False

    with open(dic_path, 'r', encoding='utf-8-sig') as f:
        for line in f:
            line = line.rstrip('\n')

            # 區段標記
            if line.startswith('complexSymbols:'):
                in_symbols_section = False
                continue
            if line.startswith('symbols:'):
                in_symbols_section = True
                continue

            if not in_symbols_section:
                continue
            if not line or line.startswith('#'):
                continue

            parts = line.split('\t')
            if len(parts) < 2:
                continue

            identifier = parts[0]
            replacement = parts[1]

            # 跳過空替換
            if not replacement:
                continue

            # 跳過多詞識別符（如 ". sentence ending"）
            if ' ' in identifier:
                continue

            # 跳過跳脫序列（如 \0, \t, \n, \f, \r）
            if identifier.startswith('\\') and len(identifier) == 2:
                continue

            # 跳過過長的識別符
            if len(identifier) > 10:
                continue

            entries[identifier] = replacement

    return entries


# ============================================================================
# 解析 cldr.dic
# ============================================================================
def parse_cldr(dic_path):
    """解析 cldr.dic 格式

    格式（在 symbols: 區段下）：
      character \t description \t level
    例如：
      🏻\t白皮膚\tsome
      。\t句號\tall
    """
    entries = {}
    in_symbols_section = False

    with open(dic_path, 'r', encoding='utf-8-sig') as f:
        for line in f:
            line = line.rstrip('\n')

            if line.startswith('symbols:'):
                in_symbols_section = True
                continue

            if not in_symbols_section:
                continue
            if not line or line.startswith('#'):
                continue

            parts = line.split('\t')
            if len(parts) < 2:
                continue

            character = parts[0]
            description = parts[1]

            # 跳過空描述
            if not description:
                continue

            # 跳過過長的字元
            if len(character) > 10:
                continue

            entries[character] = description

    return entries


# ============================================================================
# 合併三個字典
# ============================================================================
def merge_dictionaries(char_desc, symbols, cldr):
    """合併三個字典，優先級：charDesc > symbols > cldr"""
    merged = {}

    # 優先級最低的先加入
    for key, value in cldr.items():
        merged[key] = value

    for key, value in symbols.items():
        merged[key] = value

    # 優先級最高的最後加入（覆蓋前面的）
    for key, value in char_desc.items():
        merged[key] = value

    return merged


# ============================================================================
# 生成輸出檔案
# ============================================================================
def generate_json(entries, output_path):
    """生成 JSON 格式發音表（推薦使用）"""
    with open(output_path, 'w', encoding='utf-8') as f:
        json.dump(entries, f, ensure_ascii=False, indent=0, sort_keys=True)
    print(f"  JSON 發音表：{output_path}（{len(entries)} 條）")


def generate_csv(entries, output_path):
    """生成 CSV 中介檔"""
    with open(output_path, 'w', encoding='utf-8', newline='') as f:
        writer = csv.writer(f)
        writer.writerow(['character', 'replacement'])
        for char, repl in sorted(entries.items()):
            writer.writerow([char, repl])
    print(f"  CSV 中介檔：{output_path}（{len(entries)} 條）")


def generate_swift_pronunciations(entries, output_path):
    """生成 Swift 字典格式發音表（向後相容）"""
    with open(output_path, 'w', encoding='utf-8') as f:
        f.write("// 點心粵語點字輸入法 — VoiceOver 發音表\n")
        f.write("// 由 NVDA 字典自動轉換生成（characterDescriptions.dic + symbols.dic + cldr.dic）\n")
        f.write(f"// 共 {len(entries)} 條發音規則\n\n")
        f.write("import Foundation\n\n")
        f.write("let dimsumPronunciations: [String: String] = [\n")

        for char, repl in sorted(entries.items()):
            # 跳脫 Swift 字串中的特殊字元
            swift_char = char.replace('\\', '\\\\').replace('"', '\\"').replace('\n', '\\n').replace('\r', '\\r')
            swift_repl = repl.replace('\\', '\\\\').replace('"', '\\"').replace('\n', ' ').replace('\r', ' ')
            # 截斷過長的替換文字
            if len(swift_repl) > 200:
                swift_repl = swift_repl[:200] + '...'
            f.write(f'    "{swift_char}": "{swift_repl}",\n')

        f.write("]\n")

    print(f"  Swift 發音表：{output_path}（{len(entries)} 條）")


# ============================================================================
# 主程式
# ============================================================================
def main():
    script_dir = Path(__file__).parent
    nvda_dic_dir = Path(sys.argv[1]) if len(sys.argv) > 1 else script_dir / "nvda_dic"
    output_dir = Path(sys.argv[2]) if len(sys.argv) > 2 else script_dir / "voiceover_output"

    print("=" * 60)
    print("  NVDA 字典 → VoiceOver 發音表轉換工具")
    print("  讀取 characterDescriptions.dic + symbols.dic + cldr.dic")
    print("=" * 60)

    # 檢查輸入檔案
    char_desc_path = nvda_dic_dir / "characterDescriptions.dic"
    symbols_path = nvda_dic_dir / "symbols.dic"
    cldr_path = nvda_dic_dir / "cldr.dic"

    for p in [char_desc_path, symbols_path, cldr_path]:
        if not p.exists():
            print(f"✗ 找不到 {p}")
            print("  請先執行 ./update_accessibility.sh 同步 NVDA 字典")
            sys.exit(1)

    # 解析三個字典
    print(f"\n來源目錄：{nvda_dic_dir}")

    print(f"\n① 解析 characterDescriptions.dic……")
    char_desc = parse_character_descriptions(str(char_desc_path))
    print(f"   → {len(char_desc)} 條")

    print(f"\n② 解析 symbols.dic……")
    symbols = parse_symbols(str(symbols_path))
    print(f"   → {len(symbols)} 條")

    print(f"\n③ 解析 cldr.dic……")
    cldr = parse_cldr(str(cldr_path))
    print(f"   → {len(cldr)} 條")

    # 合併
    print(f"\n④ 合併三個字典（優先級：charDesc > symbols > cldr）……")
    merged = merge_dictionaries(char_desc, symbols, cldr)
    print(f"   → 合併後共 {len(merged)} 條唯一條目")

    # 顯示統計
    only_char = len(char_desc) - len(set(char_desc.keys()) - (set(symbols.keys()) | set(cldr.keys())))
    print(f"\n   統計：")
    print(f"     characterDescriptions.dic：{len(char_desc)} 條")
    print(f"     symbols.dic：              {len(symbols)} 條")
    print(f"     cldr.dic：                 {len(cldr)} 條")
    print(f"     重複鍵（已去重）：         {len(char_desc) + len(symbols) + len(cldr) - len(merged)} 條")
    print(f"     最終唯一條目：             {len(merged)} 條")

    # 建立輸出目錄
    output_dir.mkdir(parents=True, exist_ok=True)
    print(f"\n輸出目錄：{output_dir}")

    # 生成檔案
    print(f"\n⑤ 生成發音表檔案……")
    generate_json(merged, str(output_dir / "DimsumPronunciations.json"))
    generate_csv(merged, str(output_dir / "voiceover_pronunciations.csv"))
    generate_swift_pronunciations(merged, str(output_dir / "DimsumPronunciations.swift"))

    print(f"\n✓ 轉換完成！")
    print(f"\n使用方式：")
    print(f"  1. JSON 發音表（推薦）：dimsum_vo_helper 會優先載入 JSON 格式")
    print(f"  2. Swift 發音表：向後相容，當 JSON 不存在時使用")
    print(f"  3. CSV 檔：可手動匯入或其他用途")


if __name__ == "__main__":
    main()
