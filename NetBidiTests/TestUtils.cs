namespace NetBidiTests;

static class TestUtils {
    public static string GetWebFile(string url) {
        using HttpClient client = new();
        var httpTask = Task.Run(() => client.GetAsync(url));
        httpTask.Wait();

        HttpResponseMessage responseMessage = httpTask.Result ?? throw new Exception($"Failed to get file {url}");
        responseMessage.EnsureSuccessStatusCode();

        var readTask = Task.Run(() => responseMessage.Content.ReadAsStringAsync());
        readTask.Wait();

        return readTask.Result;
    }

}