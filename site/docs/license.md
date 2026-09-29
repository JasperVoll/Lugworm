---
title: "License"
nav_order: 6
description: "The MIT License for Lugworm, and the third-party components it depends on."
permalink: /docs/license/
---

## MIT License

Copyright (c) 2026 Jasper Vollmert

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
