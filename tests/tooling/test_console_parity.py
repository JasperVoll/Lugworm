"""Source-bound host safety guards; manual Rhino rows exercise the event paths."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]


def source(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


class ConsoleParityTests(unittest.TestCase):
    def test_thermals_surface_is_gone(self):
        # 4.1.0: the machine is LDM-only. The Thermals page, the heater/fan actions and
        # the ShowThermals predicate were removed; these guards replace the two parity
        # tests that used to require them, so a re-introduction is caught rather than
        # silently accepted.
        self.assertFalse((ROOT / "Lugworm/Ui/Console/Pages/ThermalsPage.cs").exists())
        self.assertFalse((ROOT / "Lugworm/Ui/Console/DuetConsoleForm.Thermals.cs").exists())
        component = source("Lugworm/Components/DuetPrintConsoleComponent.cs")
        self.assertNotIn("void ThermalAction(", component)
        view = source("Lugworm/Ui/Console/ConsoleView.cs")
        self.assertNotIn("ShowThermals", view)
        self.assertNotIn("Thermals", view)

    def test_gate_reasons_stay_available_as_tooltips(self):
        # unchanged by the Thermals removal: a refused control carries its reason in
        # ButtonSet.Reasons, read by the form's tooltip rather than drawn on the canvas
        form = source("Lugworm/Ui/Console/DuetConsoleForm.cs")
        self.assertIn('buttons.Reasons.TryGetValue', form)

    def test_validation_has_its_own_offline_gate(self):
        # 3.0.0 shell: the Job page owns the stage buttons
        view = source("Lugworm/Ui/Console/Pages/JobPage.cs")
        self.assertIn('b.Validate, "validate"', view)
        state = source("Lugworm.Core/Duet/PrintConsoleState.cs")
        self.assertIn('bool validate = Req("validate", !pending', state)
        gate = state.split('bool validate =')[1].split(';')[0]
        self.assertNotIn('connected', gate)
        self.assertIn('Plannable("validate")', gate)

    def test_start_label_comes_from_projected_mode(self):
        self.assertIn('s.StartLabel, ConsoleRole.Primary, b.Start, "start"', source("Lugworm/Ui/Console/Pages/JobPage.cs"))
        self.assertIn('.WithDryRun(_dryRun)', source("Lugworm/Components/DuetPrintConsoleComponent.cs"))

    def test_calibration_paging_and_exact_list_invalidation(self):
        view = source("Lugworm/Ui/Console/Pages/ControlPage.cs")
        self.assertIn('"pointnext"', view)
        self.assertIn('"pointprevious"', view)
        component = source("Lugworm/Components/DuetPrintConsoleComponent.cs")
        self.assertIn('if (!SamePoints(_points, points))', component)
        self.assertIn('_form?.ClearPointSelection()', component)

    def test_print_start_goto_takes_the_validated_machine_point(self):
        # 2026-09-16: "Go to print start" drives to the validated job's first extruding
        # move at Z 0. The target is the gate's own machine-frame point, so no Rhino
        # conversion may touch it on the way out, and it goes through the same planner
        # and gate set as the point go-to.
        view = source("Lugworm/Ui/Console/Pages/JobPage.cs")
        self.assertIn('b.GoToStart, "gotostart"', view)
        self.assertNotIn('"gotostart"', source("Lugworm/Ui/Console/Pages/ControlPage.cs"))
        component = source("Lugworm/Components/DuetPrintConsoleComponent.cs")
        self.assertIn('case "gotostart": GoToStart(); return;', component)
        body = component.split('void GoToStart()')[1].split('void ClearLog()')[0]
        self.assertIn('JogPlanner.PlanPath(', body)
        self.assertIn('validation.StartPoint', body)
        self.assertNotIn('MachineFrame.ToMachine', body)
        state = source("Lugworm.Core/Duet/PrintConsoleState.cs")
        self.assertIn('Req("gotostart", gotoSupported', state)
        self.assertIn('Req("gotostart", validation != null && !validation.Danger', state)

    def test_hold_released_on_all_host_lifecycle_paths(self):
        form = source("Lugworm/Ui/Console/DuetConsoleForm.cs")
        for marker in ['OnDeactivate(EventArgs e)', 'OnVisibleChanged(EventArgs e)',
                       'OnSurfaceKindChanged(SurfaceKind kind)', 'Dispose(bool disposing)']:
            body = form.split(marker)[1].split('\n        }')[0]
            self.assertIn('ReleaseHeldJog()', body, marker)
        self.assertIn('SurfaceMouseCaptureChanged +=', form)
        self.assertIn('ReleaseHeldJog();\n                    _view.ActiveTab', form)

    def test_component_policy_is_released_independently(self):
        component = source("Lugworm/Components/DuetPrintConsoleComponent.cs")
        for marker in ['void OnFormHidden()', 'void OnFormDeactivated(', 'void TearDown()']:
            self.assertIn('CancelJogHold()', component.split(marker)[1].split('\n        }')[0], marker)

    def test_whole_paint_lifecycle_is_guarded(self):
        host = source("Lugworm/Ui/Console/SkiaSurfaceHost.cs")
        self.assertIn('class GuardedGlControl : SKGLControl', host)
        self.assertIn('class GuardedCpuControl : SKControl', host)
        self.assertEqual(2, host.count('try { base.OnPaint(e); }'))
        self.assertIn('catch (Exception ex) { _failed(ex); }', host)

    def test_console_outputs_are_emitted_by_name_with_three_pinned(self):
        component = source("Lugworm/Components/DuetPrintConsoleComponent.cs")
        emit = component.split('protected override void Emit(IGH_DataAccess DA, object result)')[1].split('\n        }')[0]
        # An index would publish a temperature into a point socket the first time
        # somebody hides Motion; every value goes out by name or not at all.
        self.assertNotIn('DA.SetData(', emit)
        self.assertNotIn('DA.SetDataList(', emit)
        self.assertIn('SetOut(DA, ConsoleOutputNames.Status', emit)
        self.assertIn('SetOutList(DA, ConsoleOutputNames.Gcode', emit)
        self.assertEqual(3, component.count('.Pin(),'), 'exactly host, status and gcode are pinned')
        names = source("Lugworm.Core/Duet/ConsoleOutputs.cs")
        self.assertIn('Default = new[] { Host, Status, Gcode }', names)

    def test_recent_print_selection_loads_and_never_starts(self):
        component = source("Lugworm/Components/DuetPrintConsoleComponent.cs")
        select = component.split('void SelectRun(string id)')[1].split('void AdoptRun(')[0]
        self.assertIn('ReprintPolicy.RefuseReprint(', select)
        self.assertNotIn('StartPrintAsync', select)
        self.assertNotIn('SimulatePrintAsync', select)
        adopt = component.split('void AdoptRun(')[1].split('void ClearRunSelection')[0]
        self.assertIn('ReprintPolicy.VerifyBytes(', adopt)
        self.assertNotIn('StartPrintAsync', adopt)
        # The window offers a selection only where the gate allows one.
        self.assertIn('b.History', source("Lugworm/Ui/Console/Pages/JobPage.cs"))

    def test_the_run_archive_is_written_by_the_retention_worker(self):
        session = source("Lugworm/Duet/MachineSession.cs")
        route = session.split('void RouteRetention(')[1].split('\n        }')[0]
        # Enqueued, never written here: RouteRetention runs on the poll loop.
        self.assertIn('service.ArchiveJobGcode(', route)
        self.assertIn('Jobs.StartExternal(', route)
        self.assertIn('Jobs.EndRun();', route)
        service = source("Lugworm/Duet/LogRetentionService.cs")
        self.assertIn('Enqueue(new Command(Op.Archive', service)
        self.assertIn('case Op.Archive:', service)

    def test_reset_rechecked_with_transaction_snapshot(self):
        component = source("Lugworm/Components/DuetPrintConsoleComponent.cs")
        terminal = component.split('void TerminalSend(string line)')[1]
        transaction = terminal.split('RunGatedCommandAsync(false, async (snap, client, ct) =>')[1]
        before_send = transaction.split('SendCodeNoReplyAsync')[0]
        self.assertIn('TerminalPolicy.IsReset(verdict.Line)', before_send)
        self.assertIn('snap.Status != MachineStatus.Halted', before_send)
        self.assertIn('snap.Status != MachineStatus.Off', before_send)


if __name__ == "__main__":
    unittest.main()
