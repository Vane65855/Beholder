# 001: K&R Brace Style Over Allman

## Context

C# convention (and Visual Studio default) uses Allman style — opening braces on their own line. This increases vertical space consumption by roughly 30% without adding information. The team finds K&R (same-line braces) more readable and compact, especially on screens that show side-by-side code or have limited vertical space.

## Decision

All C# code in Beholder NMT uses K&R brace style. The `.editorconfig` enforces `csharp_new_line_before_open_brace = none`. Single-expression `if`/`else`/`foreach`/`while` bodies are written on one line without braces when they fit within 120 characters.

## Consequences

- Code is more vertically compact and scannable
- Contradicts the C# community default — new contributors familiar only with Allman may need to adjust
- The `.editorconfig` handles auto-formatting in most editors, reducing friction
- `dotnet format` respects the `.editorconfig` settings and will reformat on CI if needed
