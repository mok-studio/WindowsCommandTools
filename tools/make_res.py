#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成 Win32 资源文件 build/app.res —— 不需要 rc.exe / Windows SDK。

产物包含三类资源：

* ``RT_ICON`` (3)        —— ``ui/app.ico`` 里的 6 个图像各写一条，ID 从 1 开始；
* ``RT_GROUP_ICON`` (14) —— ID 为 1 的图标组，按顺序引用上面的 RT_ICON；
* ``RT_VERSION`` (16)    —— VERSIONINFO，即资源管理器「属性 → 详细信息」里显示的
                            「文件说明 / 产品名称 / 文件版本 / 版权」等字段。

背景：``csc.exe /win32icon:`` 只会生成一个最小的 VERSIONINFO（仅 OriginalFilename /
InternalName），所以「文件说明」永远是空的。用本脚本生成包含完整 VERSIONINFO 的
``.res``，再让 csc 用 ``/win32res:`` 直接吃进去即可（两者不能同时使用）。

格式要点（实测踩坑记录，很重要）
--------------------------------------------------------------
1. RES 条目头部尾部是 **16 字节**，不是 5 个 DWORD::

       DWORD DataVersion | WORD MemoryFlags | WORD LanguageId
       DWORD Version     | DWORD Characteristics

   类型/名称都用序号时 HeaderSize = 8 + 4 + 4 + 16 = 32。
   若按 20 字节写，csc.exe 会报 CS1583「不是有效的 Win32 资源文件」，
   cvtres.exe 会报 CVT1105 "cannot seek in file"。
2. 每个 .res 文件**开头**要有一个「空条目」（type/name 均为 0、DataSize 为 0，
   共 32 字节），缺少它 csc 同样拒绝整个文件。
3. MemoryFlags 沿用 rc.exe 的实际取值：RT_ICON=0x1010、RT_GROUP_ICON=0x1030、
   RT_VERSION=0x0030。
4. GRPICONDIR 里 planes 为 0 时写成 1（rc.exe 就是这么规范化的）。

按以上各点生成的 app.res 与 ``rc.exe /r`` 从等价 .rc 生成的 .res 只差 3 个字节：
StringTable 键大小写、以及 rc.exe 不把「值之后的对齐填充」计入叶子节点的 wLength
（本脚本按规范把它算进 wLength，Windows 两种都能正确解析）。用 ``fc /b`` 可复核。

用法::

    python tools/make_res.py                        # → build/app.res
    python tools/make_res.py --out build/app.res    # 指定输出
    python tools/make_res.py --ico ui/app.ico       # 指定图标

只依赖 Python 标准库（struct）。所有路径都相对于本脚本所在目录的上一级（仓库根）。
"""

import argparse
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

DEFAULT_ICO = os.path.join(ROOT, 'ui', 'app.ico')
DEFAULT_OUT = os.path.join(ROOT, 'build', 'app.res')

# --------------------------------------------------------------------- 常量定义
RT_ICON = 3
RT_GROUP_ICON = 14
RT_VERSION = 16

LANG_EN_US = 0x0409        # 资源语言：en-US
CODEPAGE_UNICODE = 1200    # VarFileInfo\Translation 的代码页：1200 = Unicode

# MemoryFlags 是 16 位 NE 风格的位标志（MOVEABLE 0x0010 / PURE 0x0020 /
# DISCARDABLE 0x1000）。下面三个值与 rc.exe 的实际输出一致。
MEMORY_FLAGS_ICON = 0x1010        # MOVEABLE | DISCARDABLE
MEMORY_FLAGS_GROUP_ICON = 0x1030  # MOVEABLE | DISCARDABLE | PURE
MEMORY_FLAGS_VERSION = 0x0030     # MOVEABLE | PURE

ICON_GROUP_ID = 1          # RT_GROUP_ICON 的资源 ID
VERSION_ID = 1             # RT_VERSION 的资源 ID
FIRST_ICON_ID = 1          # 第一个 RT_ICON 的 ID

FILE_VERSION = (1, 0, 0, 0)
PRODUCT_VERSION = (1, 0, 0, 0)

# VERSIONINFO 里 StringFileInfo 的字段（键 → 值），顺序即写入顺序
STRING_TABLE_KEY = '040904B0'   # 0x0409 (en-US) + 0x04B0 (1200, Unicode)


def _ver_to_str(version):
    """(1, 0, 0, 0) → '1.0.0.0'。"""
    return '%d.%d.%d.%d' % version


def _ver_to_ms_ls(version):
    """(1, 0, 0, 0) → (0x00010000, 0x00000000)，即 VS_FIXEDFILEINFO 的 MS/LS 对。"""
    return ((version[0] << 16) | version[1], (version[2] << 16) | version[3])


VERSION_STRINGS = (
    ('FileDescription', 'PowerShell and CMD command execution tools'),
    ('ProductName', 'WindowsCommandTools'),
    ('OriginalFilename', 'WindowsCommandTools.exe'),
    ('InternalName', 'WindowsCommandTools'),
    ('FileVersion', _ver_to_str(FILE_VERSION)),
    ('ProductVersion', _ver_to_str(PRODUCT_VERSION)),
    ('LegalCopyright',
     'Copyright (C) 2026 WindowsCommandTools contributors. Licensed under AGPL-3.0.'),
    ('CompanyName', 'WindowsCommandTools contributors'),
    ('Comments', 'Visual command toolbox for CMD and PowerShell'),
)


# --------------------------------------------------------------------- 对齐工具
def align4(data):
    """把 bytes 补齐到 4 字节边界（Win32 资源要求按 DWORD 对齐）。"""
    return data + b'\x00' * (-len(data) % 4)


def _align4_len(n):
    return (n + 3) & ~3


# --------------------------------------------------------------------- RES 文件
def res_id(value):
    """资源类型/名称字段：int → 序号形式（0xFFFF + WORD），str → UTF-16LE 以 0 结尾。"""
    if isinstance(value, int):
        return struct.pack('<HH', 0xFFFF, value & 0xFFFF)
    if isinstance(value, str):
        return value.encode('utf-16-le') + b'\x00\x00'
    raise TypeError('资源类型/名称只能是 int 或 str：%r' % (value,))


def res_entry(type_id, name_id, data, lang=LANG_EN_US, memory_flags=MEMORY_FLAGS_VERSION):
    """构造一条 Win32 RES 资源条目。

    布局（逐条首尾相接）::

        DWORD DataSize                 数据字节数（不含尾部对齐填充）
        DWORD HeaderSize               本条目开头到数据开始处的字节数
        类型     WORD 0xFFFF + WORD 序号  或  UTF-16LE 字符串（以 0 结尾）
        名称     同上
        （按 DWORD 对齐的填充）
        DWORD DataVersion              = 0
        WORD  MemoryFlags
        WORD  LanguageId
        DWORD Version                  = 0
        DWORD Characteristics          = 0
        数据
        （按 DWORD 对齐的填充）

    条目起点始终是 4 字节对齐的，所以按条目内偏移对齐即等价于按文件偏移对齐。
    """
    type_field = res_id(type_id)
    name_field = res_id(name_id)

    prefix = 8 + len(type_field) + len(name_field)       # 两个 DWORD + 类型 + 名称
    padding = -prefix % 4                                # 让尾部字段落在 4 字节边界
    header_tail = struct.pack('<IHHII', 0, memory_flags, lang, 0, 0)
    header_size = prefix + padding + len(header_tail)

    header = struct.pack('<II', len(data), header_size)
    header += type_field + name_field + b'\x00' * padding + header_tail

    assert len(header) == header_size, (len(header), header_size)
    assert header_size % 4 == 0, header_size
    return align4(header + data)


def null_entry():
    """rc.exe 在每个 .res 文件开头都会写一个「空条目」——type/name 均为 0、无数据。

    这不是可有可无的填充：csc.exe 需要它，缺了会报 CS1583。
    """
    return res_entry(0, 0, b'', lang=0, memory_flags=0)


# --------------------------------------------------------------------- ICO 解析
def parse_ico(path):
    """解析 .ico，返回图像列表（含 ICONDIRENTRY 各字段与原始字节）。

    ICONDIR:      WORD reserved(=0) / WORD type(=1) / WORD count
    ICONDIRENTRY: 宽 1 / 高 1 / 颜色数 1 / 保留 1 / planes 2 / bpp 2 / 字节数 4 / 偏移 4
                  （宽、高为 1 字节，0 表示 256）
    """
    with open(path, 'rb') as fp:
        raw = fp.read()

    if len(raw) < 6:
        raise ValueError('%s 太小，不是合法的 ico' % path)
    reserved, image_type, count = struct.unpack_from('<HHH', raw, 0)
    if reserved != 0 or image_type != 1:
        raise ValueError('%s 不是 ico（reserved=%d type=%d）' % (path, reserved, image_type))
    if count == 0:
        raise ValueError('%s 里没有任何图像' % path)

    images = []
    offset = 6
    for index in range(count):
        if offset + 16 > len(raw):
            raise ValueError('%s 的 ICONDIRENTRY 被截断' % path)
        (width, height, colors, _reserved, planes, bpp,
         size, data_offset) = struct.unpack_from('<BBBBHHII', raw, offset)
        offset += 16
        if data_offset + size > len(raw):
            raise ValueError('%s 第 %d 个图像越界' % (path, index))
        images.append({
            'width': width or 256,       # 0 表示 256
            'height': height or 256,
            'colors': colors,
            'planes': planes,
            'bpp': bpp,
            'data': raw[data_offset:data_offset + size],
        })
    return images


def build_group_icon(images, first_id=FIRST_ICON_ID):
    """构造 GRPICONDIR（RT_GROUP_ICON 的数据）。

    WORD reserved(=0) / WORD type(=1) / WORD count
    每条 14 字节：宽 1 / 高 1 / 颜色数 1 / 保留 1 / planes 2 / bpp 2 / 字节数 4 / ID 2

    planes 取自 ico 条目，但 0 会被规范成 1 —— rc.exe 就是这么做的
    （Pillow 生成的 ico 里 planes 写的是 0），这样两边产物才能对齐。
    """
    out = struct.pack('<HHH', 0, 1, len(images))
    for index, image in enumerate(images):
        out += struct.pack(
            '<BBBBHHIH',
            image['width'] & 0xFF,        # 256 → 0
            image['height'] & 0xFF,       # 256 → 0
            image['colors'] & 0xFF,
            0,                            # 保留字节
            (image['planes'] or 1) & 0xFFFF,   # 0 → 1（与 rc.exe 一致）
            image['bpp'] & 0xFFFF,
            len(image['data']),
            first_id + index,
        )
    return out


# --------------------------------------------------------------------- VERSIONINFO
def vi_node(key, value=b'', value_length=None, value_type=1, children=b''):
    """构造一个 VERSIONINFO 节点。

    布局::

        WORD  wLength        本节点含子节点的总长度（4 字节对齐）
        WORD  wValueLength   wType=1 时是 WCHAR 个数，wType=0 时是字节数
        WORD  wType          0 = 二进制，1 = 文本
        WCHAR szKey[]        UTF-16LE，以 0 结尾
        （填充到 4 字节边界）
        Value
        （填充到 4 字节边界）
        Children…

    注意：根节点 VS_VERSION_INFO 的 wValueLength 是 **字节数** 52（wType=0），
    而 String 节点的 wValueLength 是 **WCHAR 个数且含结尾 null**（wType=1）。
    """
    key_field = key.encode('utf-16-le') + b'\x00\x00'
    if value_length is None:
        value_length = len(value)
    if value_type == 1 and value:
        assert value.endswith(b'\x00\x00'), 'wType=1 的值必须是 0 结尾的宽字符串'

    head = struct.pack('<HHH', 0, value_length, value_type) + key_field
    head = align4(head)                                   # Padding1
    tail = align4(value) if value else b''                # Value + Padding2
    body = head + tail + children                         # body[0:6] 是占位的 wLength

    total = _align4_len(len(body))
    body += b'\x00' * (total - len(body))
    return struct.pack('<HHH', total, value_length, value_type) + body[6:]


def build_fixed_file_info():
    """VS_FIXEDFILEINFO：固定 13 个 DWORD（52 字节）。"""
    file_ms, file_ls = _ver_to_ms_ls(FILE_VERSION)
    prod_ms, prod_ls = _ver_to_ms_ls(PRODUCT_VERSION)
    return struct.pack(
        '<IIIIIIIIIIIII',
        0xFEEF04BD,      # dwSignature
        0x00010000,      # dwStrucVersion
        file_ms,         # dwFileVersionMS
        file_ls,         # dwFileVersionLS
        prod_ms,         # dwProductVersionMS
        prod_ls,         # dwProductVersionLS
        0x0000003F,      # dwFileFlagsMask
        0x00000000,      # dwFileFlags
        0x00040004,      # dwFileOS = VOS_NT_WINDOWS32
        0x00000001,      # dwFileType = VFT_APP
        0x00000000,      # dwFileSubtype
        0x00000000,      # dwFileDateMS
        0x00000000,      # dwFileDateLS
    )


def build_version_info():
    """构造完整的 RT_VERSION 数据（VS_VERSION_INFO 树）。"""
    string_nodes = b''
    for name, text in VERSION_STRINGS:
        value = text.encode('utf-16-le') + b'\x00\x00'
        string_nodes += vi_node(name, value, value_length=len(text) + 1, value_type=1)

    string_table = vi_node(STRING_TABLE_KEY, children=string_nodes, value_type=1)
    string_file_info = vi_node('StringFileInfo', children=string_table, value_type=1)

    translation = vi_node('Translation',
                          struct.pack('<HH', LANG_EN_US, CODEPAGE_UNICODE),
                          value_length=4, value_type=0)
    var_file_info = vi_node('VarFileInfo', children=translation, value_type=1)

    fixed = build_fixed_file_info()
    assert len(fixed) == 52, len(fixed)
    return vi_node('VS_VERSION_INFO', fixed, value_length=len(fixed), value_type=0,
                   children=string_file_info + var_file_info)


# --------------------------------------------------------------------- 组装
def build_res(ico_path, verbose=True):
    """读取 ico，返回完整的 .res 字节串与统计信息。"""
    images = parse_ico(ico_path)

    blocks = [null_entry()]
    for index, image in enumerate(images):
        blocks.append(res_entry(RT_ICON, FIRST_ICON_ID + index, image['data'],
                                memory_flags=MEMORY_FLAGS_ICON))

    group = build_group_icon(images)
    blocks.append(res_entry(RT_GROUP_ICON, ICON_GROUP_ID, group,
                            memory_flags=MEMORY_FLAGS_GROUP_ICON))

    version = build_version_info()
    blocks.append(res_entry(RT_VERSION, VERSION_ID, version,
                            memory_flags=MEMORY_FLAGS_VERSION))

    blob = b''.join(blocks)
    assert len(blob) % 4 == 0, '整个 .res 必须 4 字节对齐'

    if verbose:
        print('图标来源      : %s' % ico_path)
        for index, image in enumerate(images):
            print('  RT_ICON %-2d  %3dx%-3d %2dbpp  %6d 字节'
                  % (FIRST_ICON_ID + index, image['width'], image['height'],
                     image['bpp'], len(image['data'])))
        print('RT_GROUP_ICON : ID %d，%d 个条目，%d 字节'
              % (ICON_GROUP_ID, len(images), len(group)))
        print('RT_VERSION    : ID %d，%d 字节（VERSIONINFO 树）' % (VERSION_ID, len(version)))
        print('资源条目总数  : %d（含开头 1 个空条目）' % len(blocks))

    return blob, images, group, version


def main(argv=None):
    parser = argparse.ArgumentParser(
        description='生成 Win32 资源文件（RT_ICON + RT_GROUP_ICON + RT_VERSION），无需 rc.exe。')
    parser.add_argument('--ico', default=DEFAULT_ICO, help='输入 ico（默认 ui/app.ico）')
    parser.add_argument('--out', default=DEFAULT_OUT, help='输出 res（默认 build/app.res）')
    parser.add_argument('--quiet', action='store_true', help='只打印最终结果')
    args = parser.parse_args(argv)

    ico_path = os.path.abspath(args.ico)
    out_path = os.path.abspath(args.out)
    if not os.path.isfile(ico_path):
        parser.error('找不到图标文件：%s' % ico_path)

    blob, images, group, version = build_res(ico_path, verbose=not args.quiet)

    out_dir = os.path.dirname(out_path)
    if out_dir and not os.path.isdir(out_dir):
        os.makedirs(out_dir)

    with open(out_path, 'wb') as fp:
        fp.write(blob)

    print('已生成        : %s' % out_path)
    print('文件大小      : %d 字节 (%.1f KB)' % (len(blob), len(blob) / 1024.0))
    return 0


if __name__ == '__main__':
    sys.exit(main())
