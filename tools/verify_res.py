#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""独立校验 build/app.res：把生成的 Win32 资源文件解析回来逐项核对。

刻意**不 import** make_res.py —— 这里按照 Win32 资源格式与 VERSIONINFO 规范重新实现
一遍解析器，这样才算真正意义上的交叉验证（自己写的字节自己读不算验证）。

用法::

    python tools/_verify_res.py                     # 校验 build/app.res
    python tools/_verify_res.py build/app.res ui/app.ico
    python tools/_verify_res.py build/app.res ui/app.ico build/golden.res   # 再和 rc.exe 产物比对

全部通过退出码 0，否则退出码 1。
"""

import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

DEFAULT_RES = os.path.join(ROOT, 'build', 'app.res')
DEFAULT_ICO = os.path.join(ROOT, 'ui', 'app.ico')

RT_ICON = 3
RT_GROUP_ICON = 14
RT_VERSION = 16
LANG_EN_US = 0x0409

EXPECTED_MEMFLAGS = {RT_ICON: 0x1010, RT_GROUP_ICON: 0x1030, RT_VERSION: 0x0030}

EXPECTED = {
    'FileDescription': 'PowerShell and CMD command execution tools',
    'ProductName': 'WindowsCommandTools',
    'OriginalFilename': 'WindowsCommandTools.exe',
    'InternalName': 'WindowsCommandTools',
    'FileVersion': '1.0.0.0',
    'ProductVersion': '1.0.0.0',
    'LegalCopyright':
        'Copyright (C) 2026 WindowsCommandTools contributors. Licensed under AGPL-3.0.',
    'CompanyName': 'WindowsCommandTools contributors',
    'Comments': 'Visual command toolbox for CMD and PowerShell',
}

problems = []


def check(ok, label, detail=''):
    print('  [%s] %s%s' % ('OK  ' if ok else 'FAIL', label, ('  — ' + detail) if detail else ''))
    if not ok:
        problems.append(label + (('  — ' + detail) if detail else ''))
    return ok


def align4(n):
    return (n + 3) & ~3


# --------------------------------------------------------------------- RES 解析
def read_res_id(buf, offset):
    """读类型/名称字段 → (值, 新偏移)。序号形式返回 int，字符串形式返回 str。"""
    first, = struct.unpack_from('<H', buf, offset)
    if first == 0xFFFF:
        value, = struct.unpack_from('<H', buf, offset + 2)
        return value, offset + 4
    end = offset
    while True:
        if end + 2 > len(buf):
            raise ValueError('字符串型资源名没有结束符')
        if buf[end:end + 2] == b'\x00\x00':
            break
        end += 2
    return buf[offset:end].decode('utf-16-le'), end + 2


def parse_res(path):
    """解析整个 .res，返回 (原始字节, 条目列表)；顺带校验结构自洽性。"""
    with open(path, 'rb') as fp:
        buf = fp.read()

    entries = []
    offset = 0
    while offset < len(buf):
        if offset + 8 > len(buf):
            raise ValueError('偏移 %d 处条目头被截断' % offset)
        data_size, header_size = struct.unpack_from('<II', buf, offset)

        if header_size < 8 + 4 + 4 + 16:
            raise ValueError('偏移 %d 的 HeaderSize=%d 太小' % (offset, header_size))
        if header_size % 4 != 0:
            raise ValueError('偏移 %d 的 HeaderSize=%d 未按 DWORD 对齐' % (offset, header_size))

        type_id, p = read_res_id(buf, offset + 8)
        name_id, p = read_res_id(buf, p)
        if p > offset + header_size - 16:
            raise ValueError('偏移 %d 的类型/名称字段越过了数据区' % offset)
        if align4(p) != offset + header_size - 16:
            raise ValueError('偏移 %d 的类型/名称字段与头部尾部之间的填充不正确' % offset)

        # 头部尾部：DWORD DataVersion | WORD MemoryFlags | WORD LanguageId
        #           DWORD Version     | DWORD Characteristics   —— 共 16 字节
        data_version, mem_flags, lang_id, version, characteristics = struct.unpack_from(
            '<IHHII', buf, offset + header_size - 16)

        data_start = offset + header_size
        data_end = data_start + data_size
        if data_end > len(buf):
            raise ValueError('偏移 %d 的数据越界' % offset)

        entries.append({
            'offset': offset,
            'type': type_id,
            'name': name_id,
            'data': buf[data_start:data_end],
            'header_size': header_size,
            'DataVersion': data_version,
            'MemoryFlags': mem_flags,
            'LanguageId': lang_id,
            'Version': version,
            'Characteristics': characteristics,
        })
        offset = align4(data_end)

    return buf, entries


# --------------------------------------------------------------------- ICO 解析
def parse_ico(path):
    with open(path, 'rb') as fp:
        raw = fp.read()
    reserved, image_type, count = struct.unpack_from('<HHH', raw, 0)
    if (reserved, image_type) != (0, 1):
        raise ValueError('%s 不是 ico' % path)
    images = []
    offset = 6
    for _ in range(count):
        (width, height, colors, rsv, planes, bpp,
         size, data_offset) = struct.unpack_from('<BBBBHHII', raw, offset)
        offset += 16
        images.append({'width': width, 'height': height, 'colors': colors,
                       'planes': planes, 'bpp': bpp, 'size': size,
                       'data': raw[data_offset:data_offset + size]})
    return images


# ----------------------------------------------------------------- VERSIONINFO 解析
def parse_vi_node(buf, offset, limit):
    """递归解析一个 VERSIONINFO 节点 → dict。"""
    if offset + 6 > limit:
        raise ValueError('节点头越界 @%d' % offset)
    w_length, w_value_length, w_type = struct.unpack_from('<HHH', buf, offset)
    if w_length == 0 or w_length % 4 != 0:
        raise ValueError('@%d wLength=%d 非法（必须非 0 且 4 字节对齐）' % (offset, w_length))
    if offset + w_length > limit:
        raise ValueError('@%d wLength=%d 超出父节点' % (offset, w_length))

    node_end = offset + w_length

    p = offset + 6
    while True:
        if p + 2 > node_end:
            raise ValueError('@%d 的 szKey 没有结束符' % offset)
        if buf[p:p + 2] == b'\x00\x00':
            break
        p += 2
    key = buf[offset + 6:p].decode('utf-16-le')
    p = align4(p + 2)

    value_bytes = w_value_length * 2 if w_type == 1 else w_value_length
    if p + value_bytes > node_end:
        raise ValueError('@%d 的值越界（需要 %d 字节）' % (offset, value_bytes))
    value = buf[p:p + value_bytes]

    node = {'key': key, 'wLength': w_length, 'wValueLength': w_value_length,
            'wType': w_type, 'value': value,
            'string': value.decode('utf-16-le') if w_type == 1 else None,
            'children': []}

    if w_type == 1 and value and not value.endswith(b'\x00\x00'):
        raise ValueError('@%d 文本值没有以 null 结尾' % offset)

    child = align4(p + value_bytes)
    while child < node_end:
        sub = parse_vi_node(buf, child, node_end)
        node['children'].append(sub)
        child = align4(child + sub['wLength'])

    return node


def walk(node, prefix=''):
    yield prefix + node['key'], node
    for child in node['children']:
        for item in walk(child, prefix + node['key'] + '\\'):
            yield item


# --------------------------------------------------------------------- 主流程
def main(argv):
    res_path = os.path.abspath(argv[0]) if len(argv) > 0 else DEFAULT_RES
    ico_path = os.path.abspath(argv[1]) if len(argv) > 1 else DEFAULT_ICO
    golden_path = os.path.abspath(argv[2]) if len(argv) > 2 else None

    print('=' * 78)
    print('校验目标 res : %s' % res_path)
    print('对照 ico     : %s' % ico_path)
    if golden_path:
        print('对照 golden  : %s' % golden_path)
    print('=' * 78)

    if not os.path.isfile(res_path):
        print('找不到 %s' % res_path)
        return 1
    if not os.path.isfile(ico_path):
        print('找不到 %s' % ico_path)
        return 1

    blob, entries = parse_res(res_path)
    ico_images = parse_ico(ico_path)

    # ---------------------------------------------------------- 1. 整体结构
    print('\n[1] RES 整体结构')
    check(len(blob) > 0, '文件非空', '%d 字节' % len(blob))
    check(len(blob) % 4 == 0, '文件长度按 DWORD 对齐', '%d 字节' % len(blob))
    check(len(entries) == 9, '条目总数 = 1 空条目 + 6 RT_ICON + 1 组图标 + 1 版本',
          '实际 %d' % len(entries))
    check(all(e['header_size'] == 32 for e in entries),
          '所有条目 HeaderSize = 32（类型/名称均为序号：8+4+4+16）',
          '取值为 %s' % sorted({e['header_size'] for e in entries}))
    check(all(e['DataVersion'] == 0 and e['Version'] == 0 and e['Characteristics'] == 0
              for e in entries), 'DataVersion / Version / Characteristics 全为 0')
    end = max(e['offset'] + e['header_size'] + len(e['data']) for e in entries)
    check(align4(end) == len(blob), '条目无缝铺满整个文件',
          '覆盖 %d/%d 字节' % (align4(end), len(blob)))

    first = entries[0]
    check(first['type'] == 0 and first['name'] == 0 and len(first['data']) == 0
          and first['header_size'] == 32 and first['MemoryFlags'] == 0
          and first['LanguageId'] == 0,
          '文件开头是空的「终止条目」（type=0/name=0/DataSize=0，rc.exe 的写法）')

    check(all(e['LanguageId'] == LANG_EN_US for e in entries if e is not first),
          'LanguageId 全为 0x0409 (en-US)',
          '取值为 %s' % sorted({hex(e['LanguageId']) for e in entries if e is not first}))
    flags_ok = all(e['MemoryFlags'] == EXPECTED_MEMFLAGS[e['type']]
                   for e in entries if e['type'] in EXPECTED_MEMFLAGS)
    check(flags_ok, 'MemoryFlags 与 rc.exe 一致（ICON=0x1010/组=0x1030/版本=0x0030）',
          '实际 %s' % {e['type']: hex(e['MemoryFlags']) for e in entries if e['type']})

    print('  条目清单：')
    for e in entries:
        print('    +%-6d RT_%-4d 名称 %-6s 头部 %2d 字节  数据 %6d 字节  MemFlags=0x%04X lang=0x%04X'
              % (e['offset'], e['type'], str(e['name']), e['header_size'],
                 len(e['data']), e['MemoryFlags'], e['LanguageId']))

    # ---------------------------------------------------------- 2. RT_ICON
    print('\n[2] RT_ICON (3)')
    icons = [e for e in entries if e['type'] == RT_ICON]
    check(len(icons) == 6, 'RT_ICON 条目数为 6', '实际 %d' % len(icons))
    check([e['name'] for e in icons] == [1, 2, 3, 4, 5, 6],
          'RT_ICON 的 ID 为 1..6', '实际 %s' % [e['name'] for e in icons])
    check(len(icons) == len(ico_images) and all(
        icon['data'] == image['data'] for icon, image in zip(icons, ico_images)),
        '每个 RT_ICON 的字节与 ico 中对应图像完全一致')

    # ---------------------------------------------------------- 3. RT_GROUP_ICON
    print('\n[3] RT_GROUP_ICON (14)')
    groups = [e for e in entries if e['type'] == RT_GROUP_ICON]
    check(len(groups) == 1, 'RT_GROUP_ICON 条目数为 1', '实际 %d' % len(groups))
    if groups:
        g = groups[0]
        check(g['name'] == 1, 'RT_GROUP_ICON 的 ID 为 1', '实际 %s' % str(g['name']))
        data = g['data']
        grp_reserved, grp_type, grp_count = struct.unpack_from('<HHH', data, 0)
        check(grp_reserved == 0, 'GRPICONDIR.reserved = 0')
        check(grp_type == 1, 'GRPICONDIR.type = 1 (图标)')
        check(grp_count == 6, 'GRPICONDIR.count = 6', '实际 %d' % grp_count)
        check(len(data) == 6 + 14 * grp_count, 'RT_GROUP_ICON 数据长度 = 6+14*count',
              '%d 字节' % len(data))
        ok = True
        for index in range(grp_count):
            (w, h, colors, rsv, planes, bpp, size, rid) = struct.unpack_from(
                '<BBBBHHIH', data, 6 + 14 * index)
            src = ico_images[index]
            same = (w == src['width'] and h == src['height'] and colors == src['colors']
                    and planes == (src['planes'] or 1) and (planes == 1)
                    and bpp == src['bpp'] and size == src['size'] and rid == index + 1)
            ok = ok and same
            print('    条目 %d: %3dx%-3d %2dbpp planes=%d size=%-6d → RT_ICON ID %d  %s'
                  % (index, w or 256, h or 256, bpp, planes, size, rid,
                     'OK' if same else 'FAIL'))
        check(ok, '每组条目与 ico 的 ICONDIRENTRY 一致（planes 规范化为 1），且依次引用 ID 1..6')

    # ---------------------------------------------------------- 4. RT_VERSION
    print('\n[4] RT_VERSION (16)')
    versions = [e for e in entries if e['type'] == RT_VERSION]
    check(len(versions) == 1, 'RT_VERSION 条目数为 1', '实际 %d' % len(versions))
    if not versions:
        return report()

    v = versions[0]
    check(v['name'] == 1, 'RT_VERSION 的 ID 为 1', '实际 %s' % str(v['name']))
    vdata = v['data']

    try:
        root = parse_vi_node(vdata, 0, len(vdata))
        parsed_ok, err = True, ''
    except Exception as exc:                                    # noqa: BLE001
        root, parsed_ok, err = None, False, str(exc)
    check(parsed_ok, 'VERSIONINFO 树可以完整递归解析', err)
    if not parsed_ok:
        return report()

    check(root['wLength'] == len(vdata), '根节点 wLength = 数据长度',
          '%d vs %d' % (root['wLength'], len(vdata)))
    check(root['key'] == 'VS_VERSION_INFO', '根节点 key = VS_VERSION_INFO', root['key'])
    check(root['wType'] == 0 and root['wValueLength'] == 52,
          '根节点 wType=0、wValueLength=52（字节）',
          'wType=%d wValueLength=%d' % (root['wType'], root['wValueLength']))

    if check(len(root['value']) == 52, 'VS_FIXEDFILEINFO 为 52 字节',
             '%d' % len(root['value'])):
        ffi = struct.unpack('<IIIIIIIIIIIII', root['value'])
        names = ['dwSignature', 'dwStrucVersion', 'dwFileVersionMS', 'dwFileVersionLS',
                 'dwProductVersionMS', 'dwProductVersionLS', 'dwFileFlagsMask', 'dwFileFlags',
                 'dwFileOS', 'dwFileType', 'dwFileSubtype', 'dwFileDateMS', 'dwFileDateLS']
        expect = [0xFEEF04BD, 0x00010000, 0x00010000, 0x00000000, 0x00010000, 0x00000000,
                  0x3F, 0x0, 0x00040004, 0x1, 0x0, 0x0, 0x0]
        for name, got, want in zip(names, ffi, expect):
            check(got == want, 'VS_FIXEDFILEINFO.%-18s = 0x%08X' % (name, want),
                  '' if got == want else '实际 0x%08X' % got)

    nodes = dict(walk(root))
    check('VS_VERSION_INFO\\StringFileInfo' in nodes, '存在 StringFileInfo 节点')
    check('VS_VERSION_INFO\\StringFileInfo\\040904B0' in nodes,
          'StringTable 键为 040904B0（en-US + Unicode）')
    check('VS_VERSION_INFO\\VarFileInfo\\Translation' in nodes, '存在 VarFileInfo\\Translation')

    table_key = 'VS_VERSION_INFO\\StringFileInfo\\040904B0\\'
    found = {k[len(table_key):]: n for k, n in nodes.items()
             if k.startswith(table_key) and len(k) > len(table_key)}
    for name, want in EXPECTED.items():
        node = found.get(name)
        got = node['string'].rstrip('\x00') if node else None
        chars = node['wValueLength'] if node else 0
        ok = got == want and chars == len(want) + 1
        check(ok, '%-16s = %s' % (name, want),
              '' if ok else '实际 %r（wValueLength=%d）' % (got, chars))
    check(sorted(found) == sorted(EXPECTED), 'String 字段集合完全一致',
          '实际 %s' % sorted(found))

    tr = nodes.get('VS_VERSION_INFO\\VarFileInfo\\Translation')
    if tr is not None:
        lang, codepage = struct.unpack('<HH', tr['value'])
        check(tr['value'] == struct.pack('<HH', 0x0409, 1200),
              'Translation = 0x0409, 1200', '实际 0x%04X, %d' % (lang, codepage))
        check(tr['wValueLength'] == 4 and tr['wType'] == 0,
              'Translation 的 wValueLength=4、wType=0',
              'wValueLength=%d wType=%d' % (tr['wValueLength'], tr['wType']))

    desc = EXPECTED['FileDescription']
    check('FileDescription'.encode('utf-16-le') in vdata,
          "RT_VERSION 数据中能搜到 'FileDescription' 的 UTF-16LE 字节")
    check(desc.encode('utf-16-le') in vdata,
          'RT_VERSION 数据中能搜到文件说明文本的 UTF-16LE 字节',
          '%d 字节' % len(desc.encode('utf-16-le')))
    check(all(t.encode('utf-16-le') in vdata for t in EXPECTED.values()),
          '9 个字段的文本全部存在于 RT_VERSION 数据中')

    # ---------------------------------------------------------- 5. 与 rc.exe 产物比对
    if golden_path and os.path.isfile(golden_path):
        print('\n[5] 与 rc.exe 生成的 golden.res 比对')
        with open(golden_path, 'rb') as fp:
            golden = fp.read()
        check(len(blob) == len(golden), '文件长度相同',
              '%d vs %d' % (len(blob), len(golden)))
        diff = [i for i in range(min(len(blob), len(golden))) if blob[i] != golden[i]]
        check(len(diff) <= 3, '差异不超过 3 字节（键大小写 + 2 个叶子节点的 wLength）',
              '实际 %d 字节，偏移 %s' % (len(diff), ['0x%04X' % i for i in diff[:8]]))

    return report()


def report():
    print('\n' + '=' * 78)
    if problems:
        print('校验失败：%d 项不通过' % len(problems))
        for item in problems:
            print('  - %s' % item)
        print('=' * 78)
        return 1
    print('校验通过：全部检查项 OK')
    print('=' * 78)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
