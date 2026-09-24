---
title: "License"
nav_order: 6
description: "The MIT License for Lugworm, and the third-party components it depends on."
permalink: /docs/license/
---

# License

Lugworm source code and documentation are released under the MIT License.
Jasper Vollmert is the software's author and developer. The software was
created in the course of his work at the Institute of Digital and Autonomous
Construction (IDAC), Hamburg University of Technology (TUHH), using university
resources. The supervising professor explicitly approved its release under MIT.

TUHH holds the economic rights in the employee-created software under
[Section 69b of the German Copyright Act (UrhG)](https://www.gesetze-im-internet.de/urhg/__69b.html).
Jasper Vollmert retains his authorship. This distinction also follows
[TUHH's IP policy, Part II, Section 1](https://www.tuhh.de/t3resources/tuhh/download/universitaet/verwaltung/ra/Ordnungen_und_Richtlinien/IP_Richtlinie_der_TUHH__rein_nach_AS_Sept2023.pdf).
The documentation retains its existing Jasper Vollmert copyright notice;
the software-specific rule in Section 69b is not applied to it.
Student assistants supported testing, not software authorship. See the
[team credits]({{ '/docs/about/' | relative_url }}).

The copyright and permission notices below must accompany copies or substantial
portions of the work. The same license appears in the repository's
[LICENSE](https://github.com/JasperVoll/Lugworm/blob/main/LICENSE).
Files carrying their own license notices remain under those terms.

## MIT License

Copyright (c) 2026 Hamburg University of Technology (TUHH), software
Copyright (c) 2026 Jasper Vollmert, documentation

Software author: Jasper Vollmert
Institute of Digital and Autonomous Construction (IDAC), TUHH

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Third-party components

The distributed package includes or depends on third-party components under
their own licenses. Clipper2 is vendored under the Boost Software License 1.0;
Math.NET Numerics, SkiaSharp, and OpenTK use permissive licenses; the native Skia
library carries its own BSD terms. Their notices travel with the release
package. The root MIT license does not replace any of those terms.

Rhino and Grasshopper are proprietary dependencies and require a licensed Rhino
8 installation. Their assemblies are used at build/runtime as provided by
McNeel and are not licensed by this repository.

## No warranty

The MIT warranty and liability disclaimer applies to the extent permitted by
applicable law. It does not exclude liability that cannot lawfully be excluded.
In particular, German law limits advance exclusions of liability; see
[Section 276(3) BGB](https://www.gesetze-im-internet.de/bgb/__276.html) and,
where applicable, [Section 309 no. 7 BGB](https://www.gesetze-im-internet.de/bgb/__309.html).

Machine control has been tested only on our own machine. Treat any other machine
as untested, and read the [safety notes]({{ '/' | relative_url }}#safety) before
running a job.
