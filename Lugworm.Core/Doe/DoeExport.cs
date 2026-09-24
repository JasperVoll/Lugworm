using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Lugworm.Core.Doe
{
    /// <summary>Excel / CSV export of DoE plans and analysis reports, styled like the
    /// FBI2026 statistical-analysis workbook. The exported plan contains empty response
    /// columns so the same file can be filled in the lab and linked straight back into
    /// the RSM evaluation component.</summary>
    public static class DoeExport
    {
        const string AltFill = "EBF3FB", SigFill = "FCE4D6", VSigFill = "F4CCCC", CentreFill = "E2EFDA", GreyFill = "F2F2F2";

        // ── DoE plan ─────────────────────────────────────────────────────────────

        /// <param name="power">Optional Monte-Carlo power result, written to a separate
        /// "Power" sheet. The DoE_Plan sheet is byte-for-byte what it is without it.</param>
        public static void PlanToXlsx(DoePlan p, IReadOnlyList<string> responseNames, string path, PowerResult? power = null)
        {
            var wb = new XlsxWorkbook();
            var sh = wb.AddSheet("DoE_Plan");
            int m = p.FactorCount;
            int nCols = 1 + m + m + responseNames.Count;

            sh.Set(1, 1, $"{p.Design} - {p.RunCount} runs", XFmt.Title);
            sh.Merge(1, 1, 1, nCols);
            sh.Set(2, 1, p.Notes + "  |  fill the response columns after printing, then link this file to the RSM component", XFmt.Note);
            sh.Merge(2, 1, 2, nCols);

            // factor summary block
            int r = 4;
            sh.Set(r, 1, "Factor", XFmt.Header);
            sh.Set(r, 2, "Low (−1)", XFmt.Header);
            sh.Set(r, 3, "Centre (0)", XFmt.Header);
            sh.Set(r, 4, "High (+1)", XFmt.Header);
            r++;
            for (int j = 0; j < m; j++, r++)
            {
                sh.Set(r, 1, p.FactorNames[j], XFmt.Text(bold: true));
                sh.Set(r, 2, p.Lows[j], XFmt.Num("0.###"));
                sh.Set(r, 3, 0.5 * (p.Lows[j] + p.Highs[j]), XFmt.Num("0.###"));
                sh.Set(r, 4, p.Highs[j], XFmt.Num("0.###"));
            }
            r++;

            // run table
            int hdr = r;
            sh.Set(hdr, 1, "Run", XFmt.Header);
            for (int j = 0; j < m; j++) sh.Set(hdr, 2 + j, p.FactorNames[j], XFmt.Header);
            for (int j = 0; j < m; j++) sh.Set(hdr, 2 + m + j, "x_" + p.FactorNames[j], XFmt.Sub);
            for (int k = 0; k < responseNames.Count; k++) sh.Set(hdr, 2 + 2 * m + k, responseNames[k], XFmt.Header);
            for (int i = 0; i < p.RunCount; i++)
            {
                int rr = hdr + 1 + i;
                bool centre = p.Coded[i].All(v => Math.Abs(v) < 1e-9);
                string? fill = centre ? CentreFill : (i % 2 == 1 ? AltFill : null);
                sh.Set(rr, 1, p.RunLabels[i], XFmt.Text(fill, bold: centre));
                for (int j = 0; j < m; j++) sh.Set(rr, 2 + j, p.Actual[i][j], XFmt.Num("0.####", fill));
                for (int j = 0; j < m; j++) sh.Set(rr, 2 + m + j, p.Coded[i][j], XFmt.Num("0", fill));
                for (int k = 0; k < responseNames.Count; k++) sh.Set(rr, 2 + 2 * m + k, "", XFmt.Text(fill));
            }
            sh.ColWidth(1, 8);
            for (int j = 0; j < m; j++) { sh.ColWidth(2 + j, 14); sh.ColWidth(2 + m + j, 10); }
            for (int k = 0; k < responseNames.Count; k++) sh.ColWidth(2 + 2 * m + k, 16);
            if (power != null) PowerSheet(wb, power);
            wb.Save(path);
        }

        /// <summary>The optional power report: what was assumed (sigma, alpha, model,
        /// simulations, seed) and the estimated power per factor. Kept on its own sheet
        /// so the plan sheet a lab fills in never changes shape.</summary>
        static void PowerSheet(XlsxWorkbook wb, PowerResult r)
        {
            var sp = wb.AddSheet("Power");
            sp.Set(1, 1, "Monte-Carlo power of this plan", XFmt.Title);
            sp.Merge(1, 1, 1, 4);
            sp.Set(2, 1, "probability that the design detects the stated low-to-high effect at alpha, given the residual sigma - assumptions, not measurements", XFmt.Note);
            sp.Merge(2, 1, 2, 4);
            int row = 4;
            void Pair(string label, object? value, XFmt fmt)
            {
                sp.Set(row, 1, label, XFmt.Text(bold: true));
                sp.Set(row, 2, value, fmt);
                row++;
            }
            Pair("sigma (residual std. dev.)", r.Sigma, XFmt.Num("0.####"));
            Pair("alpha", r.Alpha, XFmt.Num("0.###"));
            Pair("model", r.ModelType.ToString(), XFmt.Text());
            Pair("simulations", r.NSims, XFmt.Num("0"));
            Pair("seed", r.Seed, XFmt.Num("0"));
            Pair("MC error (approx.)", r.McError, XFmt.Num("0.###"));
            row++;
            sp.Set(row, 1, "Factor", XFmt.Header);
            sp.Set(row, 2, "Effect (low to high)", XFmt.Header);
            sp.Set(row, 3, "Power", XFmt.Header);
            sp.Set(row, 4, "Note", XFmt.Header);
            row++;
            for (int j = 0; j < r.FactorNames.Length; j++, row++)
            {
                bool aliased = double.IsNaN(r.Power[j]);
                string? fill = !aliased && r.Power[j] < 0.8 ? SigFill : null;
                sp.Set(row, 1, r.FactorNames[j], XFmt.Text(fill, bold: true));
                sp.Set(row, 2, r.EffectSizes[j], XFmt.Num("0.####", fill));
                if (aliased) sp.Set(row, 3, "aliased", XFmt.Text(fill));
                else sp.Set(row, 3, r.Power[j], XFmt.Num("0.00", fill));
                sp.Set(row, 4, aliased ? "term aliased in this design under the chosen model"
                              : r.Power[j] < 0.8 ? "below the conventional 0.8 - add runs, widen ranges, or accept the risk" : "", XFmt.Text(fill));
            }
            if (r.Notes.Length > 0) { row++; sp.Set(row, 1, "note: " + r.Notes, XFmt.Note); sp.Merge(row, 1, row, 4); }
            sp.ColWidth(1, 28); sp.ColWidth(2, 20); sp.ColWidth(3, 10); sp.ColWidth(4, 60);
        }

        public static void PlanToCsv(DoePlan p, IReadOnlyList<string> responseNames, string path)
        {
            var headers = new List<string> { "Run" };
            headers.AddRange(p.FactorNames);
            headers.AddRange(p.FactorNames.Select(n => "x_" + n));
            headers.AddRange(responseNames);
            var rows = Enumerable.Range(0, p.RunCount).Select(i =>
            {
                var cells = new List<string> { p.RunLabels[i] };
                cells.AddRange(p.Actual[i].Select(v => v.ToString("0.######", CultureInfo.InvariantCulture)));
                cells.AddRange(p.Coded[i].Select(v => v.ToString("0.######", CultureInfo.InvariantCulture)));
                cells.AddRange(responseNames.Select(_ => ""));
                return cells.ToArray();
            });
            TableIo.WriteCsv(path, headers.ToArray(), rows);
        }

        // ── RSM report ───────────────────────────────────────────────────────────

        public static void RsmToXlsx(IReadOnlyList<RsmModel> models, string path)
        {
            var wb = new XlsxWorkbook();

            // data sheet
            if (models.Count > 0 && models[0].XActual.Length > 0)
            {
                var m0 = models[0];
                var sd = wb.AddSheet("Data");
                sd.Set(1, 1, "Model input data", XFmt.Title);
                for (int j = 0; j < m0.FactorCount; j++) sd.Set(2, 1 + j, m0.FactorNames[j], XFmt.Header);
                for (int k = 0; k < models.Count; k++) sd.Set(2, 1 + m0.FactorCount + k, models[k].ResponseName, XFmt.Sub);
                for (int i = 0; i < m0.XActual.Length; i++)
                {
                    string? fill = i % 2 == 1 ? AltFill : null;
                    for (int j = 0; j < m0.FactorCount; j++)
                        sd.Set(3 + i, 1 + j, m0.XActual[i][j], XFmt.Num("0.####", fill));
                    for (int k = 0; k < models.Count; k++)
                        if (i < models[k].Y.Length)
                            sd.Set(3 + i, 1 + m0.FactorCount + k, models[k].Y[i], XFmt.Num("0.####", fill));
                }
                for (int c = 1; c <= m0.FactorCount + models.Count; c++) sd.ColWidth(c, 15);
            }

            // coefficients sheet (responses side by side)
            var sc = wb.AddSheet("Coefficients");
            sc.Set(1, 1, "RSM model coefficients (coded units)", XFmt.Title);
            var allTerms = models.SelectMany(mm => mm.Terms.Select(t => t.Label)).Distinct().ToList();
            sc.Set(2, 1, "Term", XFmt.Header);
            for (int k = 0; k < models.Count; k++)
            {
                int c0 = 2 + k * 4;
                sc.Set(2, c0, models[k].ResponseName, XFmt.Header);
                sc.Merge(2, c0, 2, c0 + 3);
                sc.Set(3, c0, "Coef", XFmt.Sub); sc.Set(3, c0 + 1, "SE", XFmt.Sub);
                sc.Set(3, c0 + 2, "p", XFmt.Sub); sc.Set(3, c0 + 3, "Sig", XFmt.Sub);
            }
            for (int t = 0; t < allTerms.Count; t++)
            {
                sc.Set(4 + t, 1, allTerms[t], XFmt.Text(bold: allTerms[t] == "1"));
                for (int k = 0; k < models.Count; k++)
                {
                    int c0 = 2 + k * 4;
                    var term = models[k].Terms.FirstOrDefault(x => x.Label == allTerms[t]);
                    if (term == null) { sc.Set(4 + t, c0, "-", XFmt.Text()); continue; }
                    string? fill = double.IsNaN(term.P) ? null : term.P < 0.01 ? VSigFill : term.P < 0.05 ? SigFill : null;
                    sc.Set(4 + t, c0, term.Coef, XFmt.Num("0.0000", fill));
                    sc.Set(4 + t, c0 + 1, double.IsNaN(term.Se) ? (object)"-" : term.Se, XFmt.Num("0.0000", fill));
                    sc.Set(4 + t, c0 + 2, double.IsNaN(term.P) ? (object)"-" : term.P, XFmt.Num("0.0000", fill));
                    sc.Set(4 + t, c0 + 3, RsmFitter.Stars(term.P), XFmt.Text(fill, bold: true));
                }
            }
            int statRow = 4 + allTerms.Count;
            string[] statNames = { "R²", "R² adj", "R² pred", "RMSE", "PRESS", "n" };
            for (int s = 0; s < statNames.Length; s++)
            {
                sc.Set(statRow + s, 1, statNames[s], XFmt.Text(GreyFill, bold: true));
                for (int k = 0; k < models.Count; k++)
                {
                    double v = s == 0 ? models[k].R2 : s == 1 ? models[k].R2Adj : s == 2 ? models[k].R2Pred
                             : s == 3 ? models[k].Rmse : s == 4 ? models[k].Press : models[k].NRuns;
                    sc.Set(statRow + s, 2 + k * 4, double.IsNaN(v) ? (object)"-" : v, XFmt.Num(s == 5 ? "0" : "0.0000", GreyFill, bold: true));
                }
            }
            sc.ColWidth(1, 10);
            for (int k = 0; k < models.Count; k++)
                for (int c = 0; c < 4; c++) sc.ColWidth(2 + k * 4 + c, c == 3 ? 6 : 11);

            // ANOVA sheet: one block per response
            var sa = wb.AddSheet("ANOVA");
            sa.Set(1, 1, "ANOVA (partial SS) - * p<0.05  ** p<0.01  *** p<0.001", XFmt.Title);
            int row = 3;
            foreach (var mdl in models)
            {
                sa.Set(row, 1, $"{mdl.ResponseName}  ({mdl.ModelType}, n={mdl.NRuns})", XFmt.Header);
                sa.Merge(row, 1, row, 7);
                row++;
                string[] heads = { "Source", "SS", "df", "MS", "F", "p", "Sig" };
                for (int c = 0; c < heads.Length; c++) sa.Set(row, 1 + c, heads[c], XFmt.Sub);
                row++;
                foreach (var a in mdl.Anova)
                {
                    string? fill = double.IsNaN(a.P) ? null : a.P < 0.01 ? VSigFill : a.P < 0.05 ? SigFill : null;
                    bool bold = a.Source == "Model" || a.Source.StartsWith("Total", StringComparison.Ordinal) || a.Source == "Residual";
                    sa.Set(row, 1, a.Source, XFmt.Text(fill, bold));
                    sa.Set(row, 2, double.IsNaN(a.Ss) ? (object)"-" : a.Ss, XFmt.Num("0.0000", fill, bold));
                    sa.Set(row, 3, a.Df, XFmt.Num("0", fill, bold));
                    sa.Set(row, 4, double.IsNaN(a.Ms) ? (object)"-" : a.Ms, XFmt.Num("0.0000", fill, bold));
                    sa.Set(row, 5, double.IsNaN(a.F) ? (object)"-" : a.F, XFmt.Num("0.00", fill, bold));
                    sa.Set(row, 6, double.IsNaN(a.P) ? (object)"-" : a.P, XFmt.Num("0.0000", fill, bold));
                    sa.Set(row, 7, RsmFitter.Stars(a.P), XFmt.Text(fill, bold: true));
                    row++;
                }
                if (mdl.DroppedTerms.Count > 0)
                {
                    sa.Set(row, 1, "dropped: " + string.Join(", ", mdl.DroppedTerms), XFmt.Note);
                    sa.Merge(row, 1, row, 7);
                    row++;
                }
                row++;
            }
            sa.ColWidths(18, 12, 6, 12, 9, 9, 6);
            wb.Save(path);
        }

        // ── desirability report ──────────────────────────────────────────────────

        public static void DesirabilityToXlsx(DesirabilityResult res, IReadOnlyList<RsmModel> models,
            IReadOnlyList<Goal> goals, IReadOnlyList<Goal>? factorGoals, string path,
            IReadOnlyList<(string label, DesirabilityResult r)>? sweep = null)
        {
            var wb = new XlsxWorkbook();
            var sh = wb.AddSheet("Desirability");
            var m0 = models[0];
            sh.Set(1, 1, "Desirability optimisation (Derringer–Suich weighted geometric mean)", XFmt.Title);
            sh.Merge(1, 1, 1, 6);

            int r = 3;
            sh.Set(r, 1, "Recommended settings", XFmt.Header); sh.Merge(r, 1, r, 3); r++;
            for (int j = 0; j < m0.FactorCount; j++, r++)
            {
                sh.Set(r, 1, m0.FactorNames[j], XFmt.Text(bold: true));
                sh.Set(r, 2, res.XBest[j], XFmt.Num("0.####"));
                if (!double.IsNaN(res.DFactors[j])) sh.Set(r, 3, res.DFactors[j], XFmt.Num("0.00"));
            }
            sh.Set(r, 1, "Overall D", XFmt.Text(GreyFill, bold: true));
            sh.Set(r, 2, res.DOverall, XFmt.Num("0.000", GreyFill, bold: true));
            r += 2;

            sh.Set(r, 1, "Response", XFmt.Header); sh.Set(r, 2, "Goal", XFmt.Header);
            sh.Set(r, 3, "Weight", XFmt.Header); sh.Set(r, 4, "Predicted", XFmt.Header); sh.Set(r, 5, "d", XFmt.Header);
            r++;
            for (int i = 0; i < models.Count; i++, r++)
            {
                sh.Set(r, 1, models[i].ResponseName, XFmt.Text());
                sh.Set(r, 2, goals[i].Describe(), XFmt.Text());
                sh.Set(r, 3, goals[i].Weight, XFmt.Num("0.##"));
                sh.Set(r, 4, res.YPred[i], XFmt.Num("0.0000"));
                sh.Set(r, 5, double.IsNaN(res.DResponses[i]) ? (object)"-" : res.DResponses[i], XFmt.Num("0.00"));
            }
            if (res.Notes.Length > 0) { sh.Set(r + 1, 1, res.Notes, XFmt.Note); sh.Merge(r + 1, 1, r + 1, 6); }
            sh.ColWidths(24, 40, 9, 12, 8);

            if (sweep != null && sweep.Count > 0)
            {
                var ss = wb.AddSheet("Sweep");
                ss.Set(1, 1, "Recipe sweep", XFmt.Title);
                ss.Set(2, 1, "Case", XFmt.Header);
                for (int j = 0; j < m0.FactorCount; j++) ss.Set(2, 2 + j, m0.FactorNames[j], XFmt.Header);
                ss.Set(2, 2 + m0.FactorCount, "D", XFmt.Header);
                for (int k = 0; k < models.Count; k++)
                    ss.Set(2, 3 + m0.FactorCount + k, "pred. " + models[k].ResponseName, XFmt.Sub);
                for (int i = 0; i < sweep.Count; i++)
                {
                    string? fill = i % 2 == 1 ? AltFill : null;
                    ss.Set(3 + i, 1, sweep[i].label, XFmt.Text(fill, bold: true));
                    for (int j = 0; j < m0.FactorCount; j++)
                        ss.Set(3 + i, 2 + j, sweep[i].r.XBest[j], XFmt.Num("0.####", fill));
                    ss.Set(3 + i, 2 + m0.FactorCount, sweep[i].r.DOverall, XFmt.Num("0.000", fill, bold: true));
                    for (int k = 0; k < models.Count; k++)
                        ss.Set(3 + i, 3 + m0.FactorCount + k, sweep[i].r.YPred[k], XFmt.Num("0.0000", fill));
                }
                ss.ColWidth(1, 14);
                for (int c = 2; c <= 2 + m0.FactorCount + models.Count + 1; c++) ss.ColWidth(c, 14);
            }
            wb.Save(path);
        }
    }
}
