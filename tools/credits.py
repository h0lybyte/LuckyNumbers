"""content/credits.txtpb -> CREDITS.md (full) and the README.md credits block (one line per credit).

Called by `tools/ln.py gen` (and checked by `gen --check`), so the game's Credits panel, CREDITS.md and the
README never disagree. Edit content/credits.txtpb, not the Markdown.
"""

BEGIN = "<!-- credits:begin (generated from content/credits.txtpb by tools/ln.py gen; edit that file) -->"
END = "<!-- credits:end -->"


def _license(c: dict) -> str:
    lic = c.get("license", "")
    if lic and c.get("license_url"):
        return f"[{lic}]({c['license_url']})"
    return lic


def credits_md(cfg: dict) -> str:
    lines = ["# Credits", "", "<!-- Generated from content/credits.txtpb by tools/ln.py gen; edit that file. -->", ""]
    if cfg.get("intro"):
        lines += [cfg["intro"], ""]
    for section in cfg.get("sections", []):
        lines += [f"## {section.get('name', '')}", ""]
        for c in section.get("credits", []):
            head = f"- **{c.get('what', '')}**: \"{c.get('title', '')}\""
            if c.get("author"):
                head += f" by **{c['author']}**"
            if c.get("license"):
                head += f" ({_license(c)})"
            lines.append(head)
            if c.get("url"):
                lines.append(f"  {c['url']}")
            if c.get("notes"):
                lines.append(f"  {c['notes']}")
            if c.get("path"):
                lines.append(f"  (`{c['path']}`)")
        lines.append("")
    return "\n".join(lines).rstrip() + "\n"


def readme_block(cfg: dict) -> str:
    lines = [BEGIN, "## Credits", ""]
    for section in cfg.get("sections", []):
        credits = section.get("credits", [])
        if section.get("compact"):
            names = ", ".join(f"[{c.get('title', '')}]({c['url']})" if c.get("url") else c.get("title", "") for c in credits)
            licenses = sorted({c.get("license", "") for c in credits if c.get("license")})
            lines.append(f"- {section.get('name', '')}: {names}" + (f" ({', '.join(licenses)})" if licenses else ""))
            continue
        for c in credits:
            title = f"[{c.get('title', '')}]({c['url']})" if c.get("url") else c.get("title", "")
            by = f" by {c['author']}" if c.get("author") else ""
            lic = f" ({_license(c)})" if c.get("license") else ""
            lines.append(f"- {c.get('what', '')}: {title}{by}{lic}")
    lines += ["", "Details: [CREDITS.md](CREDITS.md) (also in the game: title screen, Credits).", END]
    return "\n".join(lines)


def readme(current: str, cfg: dict) -> str:
    """README with its credits block replaced (appended at the end if it has none)."""
    block = readme_block(cfg)
    if BEGIN.split(" (")[0] in current and END in current:
        start = current.index(BEGIN.split(" (")[0])
        end = current.index(END) + len(END)
        return current[:start] + block + current[end:]
    return current.rstrip() + "\n\n" + block + "\n"
