using DevExpress.ExpressApp;
using OutlookInspiredDemo.Module.BusinessObjects;

namespace XafHeadless.Api.Commands;

// Task 1 minimal retarget of the read-only summary command to the demo model (Task 2 finalizes the
// command surface + tests). Resolves an Order through the SECURED ObjectSpace the controller hands it
// and returns a computed line-item summary — real server logic under security, nothing committed.
public class OrderSummaryCommand : IHeadlessCommand {
    public string Id => "OrderSummary";

    public CommandResult Execute(IObjectSpace os, string[] objectKeys) {
        if (objectKeys.Length == 0) return new CommandResult(false, "No order selected.", []);

        var keyMember = os.TypesInfo.FindTypeInfo(typeof(Order)).KeyMember;
        var order = os.GetObjectByKey<Order>(KeyConverter.Convert(objectKeys[0], keyMember.MemberType));
        if (order is null) return new CommandResult(false, $"Order {objectKeys[0]} not found.", []);

        var count = order.OrderItems.Count;
        var total = order.OrderItems.Sum(i => i.Total);
        var message = $"Order {order.InvoiceNumber}: {count} item(s), total {total:C}";
        return new CommandResult(true, message, []);
    }
}
