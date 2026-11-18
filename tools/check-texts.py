#!/usr/bin/env python3
"""Checks the translation tables in src/RdpClipboardBridge.cs.

Per language: every text id is present and none twice, each text has exactly the {n} placeholders
of the English one, nothing equals the English text unless it is listed in INTENDED_SAME, and every
tray tooltip fits the 63 characters a NotifyIcon takes.
Standard library only, like the rest of the repo's tools. Exits 1 if anything is wrong.

    python3 tools/check-texts.py [path/to/RdpClipboardBridge.cs]
"""
import re
import sys
from pathlib import Path

# texts that may legitimately equal English: (language, text id)
INTENDED_SAME = {
    ('de', 'FileTypeVideos'),
    ('de', 'FileTypeAudio'),
    ('fr', 'FileTypeImages'),
    ('fr', 'FileTypeAudio'),
    ('fr', 'FileTypeArchives'),
    ('fr', 'FileTypeApplications'),
    ('it', 'FileTypeAudio'),
    ('es', 'FileTypeAudio'),
    ('id', 'FileTypeAudio'),
    ('de', 'ButtonOk'),
    ('fr', 'ButtonOk'),
    ('it', 'ButtonOk'),
    ('pt', 'ButtonOk'),
    ('ja', 'ButtonOk'),
    ('id', 'ButtonOk'),
}

# NotifyIcon.Text throws beyond 63 characters, so every tooltip must fit with its longest values
TOOLTIP_PREFIX = 'TrayTooltip'
TOOLTIP_MAX_LENGTH = 63
TOOLTIP_VALUES = ('RDP Clipboard Bridge', '999')

ENTRY = re.compile(r'\{\s*TextId\.(\w+),\s*"((?:[^"\\]|\\.)*)"\s*\}')


def text_ids(source):
    body = re.search(r'enum TextId\s*\{(.*?)\}', source, re.S).group(1)
    return [name.strip() for name in body.split(',') if name.strip()]


def languages(source):
    table = source[source.index('static readonly Dictionary<string, Dictionary<TextId, string>> Languages'):]
    blocks = re.findall(r'\{\s*"(\w+)",\s*new Dictionary<TextId, string>\s*\{(.*?)\n        \} \}', table, re.S)
    found = {}
    for code, body in blocks:
        entries = ENTRY.findall(body)
        names = [name for name, _ in entries]
        duplicates = sorted({name for name in names if names.count(name) > 1})
        found[code] = (dict(entries), duplicates)
    return found


def placeholders(text):
    return sorted(set(re.findall(r'\{\d+\}', text)))


def too_long_tooltips(texts):
    too_long = []
    for name, text in texts.items():
        if not name.startswith(TOOLTIP_PREFIX):
            continue
        shown = re.sub(r'\{(\d+)\}', lambda m: TOOLTIP_VALUES[int(m.group(1))], text)
        if len(shown) > TOOLTIP_MAX_LENGTH:
            too_long.append((name, len(shown)))
    return too_long


def main():
    default = Path(__file__).resolve().parent.parent / 'src' / 'RdpClipboardBridge.cs'
    path = Path(sys.argv[1]) if len(sys.argv) > 1 else default
    source = path.read_text(encoding='utf-8')

    ids = text_ids(source)
    tables = languages(source)
    english = tables['en'][0]

    problems = 0
    print('text ids: %d, languages: %s' % (len(ids), ' '.join(tables)))
    for code, (texts, duplicates) in tables.items():
        missing = [i for i in ids if i not in texts]
        unknown = [i for i in texts if i not in ids]
        wrong = [(i, placeholders(english[i]), placeholders(t)) for i, t in texts.items()
                 if i in english and placeholders(t) != placeholders(english[i])]
        same = [i for i, t in texts.items() if code != 'en' and t == english.get(i)]
        intended = [i for i in same if (code, i) in INTENDED_SAME]
        unintended = [i for i in same if (code, i) not in INTENDED_SAME]
        too_long = too_long_tooltips(texts)
        ok = not (missing or unknown or wrong or unintended or duplicates or too_long)
        problems += not ok
        print('%s: %d texts, %s' % (code, len(texts), 'ok' if ok else 'PROBLEMS'))
        for label, items in (('missing', missing), ('unknown', unknown), ('duplicate', duplicates),
                             ('placeholder mismatch', wrong), ('identical to English', unintended),
                             ('tooltip over %d characters' % TOOLTIP_MAX_LENGTH, too_long)):
            if items:
                print('   %s: %s' % (label, items))
        if intended:
            print('   identical to English, intended: %s' % intended)

    print('intended identical texts: %d' % len(INTENDED_SAME))
    sys.exit(1 if problems else 0)


main()
