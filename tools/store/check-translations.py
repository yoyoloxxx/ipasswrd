# -*- coding: utf-8 -*-
"""Ищет строки, которые приложение переводит, но перевода для них нет.

Tr("...") без записи в EnMap не падает и не подчёркивается — он просто возвращает
русский текст. В английском интерфейсе это выглядит как случайные русские вкрапления,
и заметить их можно только переключив язык и обойдя все экраны руками.

    python tools\\store\\check-translations.py
"""
import io, os, re, sys

ROOT = r"D:\MyProjects\IPasswrd\windows\IPasswrd.App"

# Ключи EnMap: ["текст"] = "text"
KEY_RE = re.compile(r'\["((?:[^"\\]|\\.)*)"\]\s*=\s*"')
# Аргументы Tr("..."): только литералы; Tr(переменная) проверить нельзя.
# Длинный текст часто разбит на два литерала через + — компилятор склеит их в один
# ключ, и проверка обязана склеить тоже, иначе она будет жаловаться на целые переведённые строки.
TR_RE = re.compile(r'Tr\(\s*("(?:[^"\\]|\\.)*"(?:\s*\+\s*"(?:[^"\\]|\\.)*")*)')
PART_RE = re.compile(r'"((?:[^"\\]|\\.)*)"')


def unescape(s):
    return s.replace('\\"', '"').replace("\\n", "\n").replace("\\\\", "\\")


def main():
    keys, used = set(), {}
    for name in sorted(os.listdir(ROOT)):
        if not name.endswith(".cs"):
            continue
        path = os.path.join(ROOT, name)
        text = io.open(path, encoding="utf-8").read()
        for m in KEY_RE.finditer(text):
            keys.add(unescape(m.group(1)))
        for m in TR_RE.finditer(text):
            lit = "".join(unescape(p) for p in PART_RE.findall(m.group(1)))
            used.setdefault(lit, set()).add(name)

    # Строку без единой кириллической буквы переводить не нужно: «CSV», «IPasswrd», «OK».
    cyr = re.compile(r"[а-яёА-ЯЁ]")
    missing = sorted((k, v) for k, v in used.items() if cyr.search(k) and k not in keys)

    for lit, files in missing:
        short = lit if len(lit) <= 70 else lit[:67] + "…"
        print("нет перевода: %-72s  (%s)" % ('"' + short + '"', ", ".join(sorted(files))))

    print("\nвсего Tr(...) с кириллицей: %d, без перевода: %d"
          % (sum(1 for k in used if cyr.search(k)), len(missing)))
    return 1 if missing else 0


if __name__ == "__main__":
    sys.exit(main())
