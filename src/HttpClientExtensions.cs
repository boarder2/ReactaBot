using System.Net;

namespace reactabot;

public static class HttpClientExtensions
{
	public static async Task<HttpResponseMessage> SendWithAuth(this HttpClient client, IConfiguration configuration, Uri uri, List<KeyValuePair<string, string>> content, bool sendAuth = false)
	{

		var localContent = new List<KeyValuePair<string, string>>(content);
		if (sendAuth)
		{
			var user = configuration.GetValue<string>("MESSAGE_USERNAME");
			var pass = configuration.GetValue<string>("MESSAGE_PASSWORD");
			if (configuration.GetValue<bool>("LOG_CREDS", false))
			{
				Console.WriteLine($"Sending reqeust with auth for user {user} and password {pass}");
			}
			localContent.AddRange(new[]
			{
				new KeyValuePair<string, string>("username", user),
				new KeyValuePair<string, string>("password", pass)
			});
		}

		// //Winchatty seems to crap itself if the Expect: 100-continue header is there.
		// request.DefaultRequestHeaders.ExpectContinue = false;
		// if (!string.IsNullOrWhiteSpace(acceptHeader))
		// {
		//     request.DefaultRequestHeaders.Add("Accept", acceptHeader);
		// }

		var items = localContent.Select(i => WebUtility.UrlEncode(i.Key) + "=" + WebUtility.UrlEncode(i.Value));
		using var formContent = new StringContent(string.Join("&", items), null, "application/x-www-form-urlencoded");

		var response = await client.PostAsync(uri, formContent).ConfigureAwait(false);
		return response;
	}
}