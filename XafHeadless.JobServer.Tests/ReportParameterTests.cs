using DevExpress.XtraReports.Parameters;
using DevExpress.XtraReports.UI;
using XafHeadless.JobServer.BusinessObjects;
using XafHeadless.JobServer.Reports;

namespace XafHeadless.JobServer.Tests;

// BUG-017: ReportsController.HintFor projects "enum" and "dateonly" parameter hints, and ReportsPanel.razor
// renders editors for them, but ReportRenderService.ApplyParameters previously delegated everything except
// Guid to Convert.ChangeType(raw, target, InvariantCulture) -- which throws InvalidCastException for any
// Enum or DateOnly parameter type because String's IConvertible implementation supports neither.
[TestClass]
public class ReportParameterTests {
    [TestMethod]
    public void ApplyParameters_converts_Enum_parameter_from_member_name() {
        using var report = new XtraReport();
        var param = new Parameter { Name = "Status", Type = typeof(JobRunStatus), Value = JobRunStatus.NeverRun };
        report.Parameters.Add(param);

        ReportRenderService.ApplyParameters(report, new Dictionary<string, string?> {
            ["Status"] = nameof(JobRunStatus.Success)
        });

        Assert.AreEqual(JobRunStatus.Success, param.Value);
    }

    [TestMethod]
    public void ApplyParameters_converts_DateOnly_parameter_from_iso_date_string() {
        using var report = new XtraReport();
        var param = new Parameter { Name = "AsOfDate", Type = typeof(DateOnly), Value = new DateOnly(2026, 1, 1) };
        report.Parameters.Add(param);

        ReportRenderService.ApplyParameters(report, new Dictionary<string, string?> {
            ["AsOfDate"] = "2026-07-15"
        });

        Assert.AreEqual(new DateOnly(2026, 7, 15), param.Value);
    }

    [TestMethod]
    public void ApplyParameters_rejects_undefined_numeric_enum_value() {
        using var report = new XtraReport();
        var param = new Parameter { Name = "Status", Type = typeof(JobRunStatus), Value = JobRunStatus.NeverRun };
        report.Parameters.Add(param);

        Assert.ThrowsExactly<FormatException>(() =>
            ReportRenderService.ApplyParameters(report, new Dictionary<string, string?> {
                ["Status"] = "9999"
            }));
    }
}
