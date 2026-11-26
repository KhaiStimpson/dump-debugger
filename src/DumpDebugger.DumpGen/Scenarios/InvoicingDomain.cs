using Microsoft.Data.SqlClient;

namespace Contoso.Invoicing
{
    public static class Db
    {
        public static string ConnectionString { get; set; } = "";
    }

    /// <summary>Renders invoice PDFs through a shared renderer that has stopped answering.</summary>
    public static class PdfRenderer
    {
        private static readonly TaskCompletionSource<byte[]> s_stuck = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static Task<byte[]> RenderAsync(string invoiceId, int lines) => s_stuck.Task;
    }

    public sealed class InvoiceExporter
    {
        public async Task<int> ExportAsync(string invoiceId)
        {
            // The bug: the connection stays open across a slow call that has nothing to do with the database.
            await using var connection = new SqlConnection(Db.ConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand("SELECT COUNT(*) FROM InvoiceLines WHERE InvoiceId = @id", connection);
            command.Parameters.AddWithValue("@id", invoiceId);
            var lines = (int)(await command.ExecuteScalarAsync() ?? 0);
            var pdf = await PdfRenderer.RenderAsync(invoiceId, lines);
            return pdf.Length;
        }
    }

    public sealed class CustomerDirectory
    {
        public int CountOrders(string customerId)
        {
            using var connection = new SqlConnection(Db.ConnectionString);
            connection.Open();
            using var command = new SqlCommand("SELECT COUNT(*) FROM Orders WHERE CustomerId = @id", connection);
            command.Parameters.AddWithValue("@id", customerId);
            return (int)command.ExecuteScalar();
        }
    }

    public static class LoyaltyPoints
    {
        /// <summary>The bug: the early return on a closed account skips Close, and nothing disposes the connection.</summary>
        public static int Balance(string customerId)
        {
            var connection = new SqlConnection(Db.ConnectionString);
            connection.Open();
            var command = new SqlCommand("SELECT Points FROM Loyalty WHERE CustomerId = @id", connection);
            command.Parameters.AddWithValue("@id", customerId);
            var points = (int)command.ExecuteScalar();
            if (customerId.StartsWith("CLOSED-", StringComparison.Ordinal))
            {
                return 0;
            }

            connection.Close();
            return points;
        }
    }
}
