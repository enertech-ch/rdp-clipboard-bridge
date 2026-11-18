#!/usr/bin/env python3
"""Checks the language files in src/lang/ against the TextId enum in src/Texts.cs.

Every src/lang/<code>.txt is a language, found the same way build.ps1 finds them. Per language:
every line is a comment, blank or "TextId = text", every text id is present and none twice or
unknown, each text has exactly the {n} placeholders of the English one and no stray brace, nothing
equals the English text unless it is listed in INTENDED_SAME, and every tray tooltip fits the 63
characters a NotifyIcon takes.
Standard library only, like the rest of the repo's tools. Exits 1 if anything is wrong.

    python3 tools/check-texts.py [path/to/repo]
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
    ('de', 'AboutVersion'),
    ('fr', 'AboutVersion'),
}

# NotifyIcon.Text throws beyond 63 characters, so every tooltip must fit with its longest values
TOOLTIP_PREFIX = 'TrayTooltip'
TOOLTIP_MAX_LENGTH = 63
TOOLTIP_VALUES = ('RDP Clipboard Bridge', '999')

REFERENCE_LANGUAGE = 'en'
LANGUAGE_FILE_NAME = re.compile(r'[a-z]{2}\.txt')
TEXT_ID = re.compile(r'\w+')
ESCAPE = re.compile(r'\\(.?)')


def text_ids(source):
    body = re.search(r'enum TextId\s*\{(.*?)\}', source, re.S).group(1)
    return [name.strip() for name in body.split(',') if name.strip()]


def unescape(text):
    """Like Texts.Unescape: \\n is a line break, \\\\ a backslash. Returns None for any other escape."""
    if any(m.group(1) not in ('n', '\\') for m in ESCAPE.finditer(text)):
        return None
    return ESCAPE.sub(lambda m: '\n' if m.group(1) == 'n' else '\\', text)


def read_language(path):
    """Parses one language file like Texts.AddLine does; returns texts, duplicates, malformed lines."""
    texts, duplicates, malformed = {}, [], []
    raw = path.read_bytes()
    if raw.startswith(b'\xef\xbb\xbf'):
        malformed.append((1, 'starts with a byte order mark; save as UTF-8 without BOM'))
    for number, line in enumerate(raw.decode('utf-8', errors='replace').lstrip('﻿').split('\n'), 1):
        line = line.strip()
        if not line or line.startswith('#'):
            continue
        name, equals, text = line.partition('=')
        name, text = name.strip(), unescape(text.strip())
        if not equals or not TEXT_ID.fullmatch(name):
            malformed.append((number, 'not "TextId = text"'))
        elif text is None:
            malformed.append((number, 'a backslash that is neither \\n nor \\\\'))
        elif name in texts:
            duplicates.append(name)
        else:
            texts[name] = text
    if '�' in raw.decode('utf-8', errors='replace'):
        malformed.append((0, 'not valid UTF-8'))
    return texts, duplicates, malformed


def placeholders(text):
    return sorted(set(re.findall(r'\{\d+\}', text)))


def has_stray_brace(text):
    return re.search(r'[{}]', re.sub(r'\{\d+\}', '', text)) is not None


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
    repo = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent
    ids = text_ids((repo / 'src' / 'Texts.cs').read_text(encoding='utf-8'))
    files = sorted((repo / 'src' / 'lang').glob('*.txt'))

    problems = 0
    badly_named = [f.name for f in files if not LANGUAGE_FILE_NAME.fullmatch(f.name)]
    if badly_named:
        print('not named <two-letter code>.txt, which build.ps1 refuses: %s' % badly_named)
        problems += 1
    tables = {f.stem: read_language(f) for f in files}
    if REFERENCE_LANGUAGE not in tables:
        print('src/lang/%s.txt is missing - English is the reference' % REFERENCE_LANGUAGE)
        sys.exit(1)
    english = tables[REFERENCE_LANGUAGE][0]

    print('text ids: %d, languages: %s' % (len(ids), ' '.join(tables)))
    for code, (texts, duplicates, malformed) in tables.items():
        missing = [i for i in ids if i not in texts]
        unknown = [i for i in texts if i not in ids]
        wrong = [(i, placeholders(english[i]), placeholders(t)) for i, t in texts.items()
                 if i in english and placeholders(t) != placeholders(english[i])]
        stray = [i for i, t in texts.items() if has_stray_brace(t)]
        same = [i for i, t in texts.items() if code != REFERENCE_LANGUAGE and t == english.get(i)]
        intended = [i for i in same if (code, i) in INTENDED_SAME]
        unintended = [i for i in same if (code, i) not in INTENDED_SAME]
        too_long = too_long_tooltips(texts)
        ok = not (malformed or missing or unknown or wrong or stray or unintended or duplicates or too_long)
        problems += not ok
        print('%s: %d texts, %s' % (code, len(texts), 'ok' if ok else 'PROBLEMS'))
        for label, items in (('malformed line', malformed), ('missing', missing), ('unknown', unknown),
                             ('duplicate', duplicates), ('placeholder mismatch', wrong),
                             ('stray brace', stray), ('identical to English', unintended),
                             ('tooltip over %d characters' % TOOLTIP_MAX_LENGTH, too_long)):
            if items:
                print('   %s: %s' % (label, items))
        if intended:
            print('   identical to English, intended: %s' % intended)

    print('intended identical texts: %d' % len(INTENDED_SAME))
    sys.exit(1 if problems else 0)


main()
