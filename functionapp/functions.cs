namespace lasercat {
  using System.Collections.Generic;
  using System.Collections.ObjectModel;
  using System.IO;
  using System.Linq;
  using System.Net.Http;
  using System.Net;
  using System.Text;
  using System.Threading.Tasks;
  using System;
  using lasercat.models;
  using Microsoft.Azure.WebJobs.Host;
  using Microsoft.Azure.WebJobs;
  using Microsoft.Extensions.Logging;
  using Microsoft.WindowsAzure.Storage.Auth;
  using Microsoft.WindowsAzure.Storage.Blob;
  using Microsoft.WindowsAzure.Storage;
  using Newtonsoft.Json.Linq;
  using Newtonsoft.Json;

  public static class functions {
    private static HttpClient client = new HttpClient ();
    private static ILogger durableLogger;

    //Set the following values by using app settings
    //ex: baseApiUri = 'https://eastus2.cris.ai'
    private static readonly Uri baseApiUri = new Uri (Environment.GetEnvironmentVariable ("SpeechApiBaseUri"));
    //ex: speechApiToken = 'abc123'
    private static string speechApiToken = Environment.GetEnvironmentVariable ("SpeechApiToken");
    private static string blobPolicyName = Environment.GetEnvironmentVariable ("BlobContainerPolicyName");
    private static string transcriptContainer = Environment.GetEnvironmentVariable ("TranscriptContainer");
    private static string processedAudioContainer = Environment.GetEnvironmentVariable ("ProcessedAudioContainer");

    private const string containerName = "recordings";

    [FunctionName ("ProcessRecording")]
    public static async void ProcessRecording (
      [BlobTrigger (containerName + "/{name}", Connection = "AzureWebJobsStorage")] Stream bytes,
      string name,
      Uri uri,
      ILogger log
    ) {
      var sasUri = GetBlobSASToken (uri, blobPolicyName, log);
      var recording = new TranscriptionDefinition { Name = name, RecordingsUrl = sasUri, Locale = "en-US", Models = new Collection<ModelIdentity> (), Description = "something" };
      var postUri = new Uri (baseApiUri, "api/speechtotext/v2.0/transcriptions");

      //submit record: /api/speechtotext/v2.0/transcriptions 
      var req = new HttpRequestMessage (HttpMethod.Post, postUri);
      req.Headers.Add ("Ocp-Apim-Subscription-Key", speechApiToken);
      req.Content = new System.Net.Http.StringContent (
        Newtonsoft.Json.JsonConvert.SerializeObject (recording),
        Encoding.UTF8,
        "application/json"
      );

      Uri ret = null;
      try {
        var resp = await client.SendAsync (req);

        if (resp.StatusCode == HttpStatusCode.Accepted) {
          ret = resp.Headers.Location;
          log.LogInformation ($"Transcript available at: {ret.AbsoluteUri}");
        } else {
          log.LogWarning ($"Transcription request not accepted: {resp.StatusCode.ToString()}");
        }
      } catch (HttpRequestException ex) {
        log.LogError ($"Speech API request failed", ex);
      }
    }

    [FunctionName ("Orchestrator_TimerStart")]
    public static async void TimerStart (
      [TimerTrigger ("*/5 * * * * *")] TimerInfo timer, [OrchestrationClient] DurableOrchestrationClient starter,
      ILogger log
    ) {
      durableLogger = log;
      var instanceId = await starter.StartNewAsync ("Orchestrator_Transcriptions", null);
      durableLogger.LogInformation ($"Started orchestration with ID = '{instanceId}'.");
    }

    [FunctionName ("Orchestrator_Transcriptions")]
    public static async Task<string> ProccessTranscriptions ([OrchestrationTrigger] DurableOrchestrationContext context) {

      //get list of transactions where status == success
      var list = await context.CallActivityAsync<IEnumerable<Transcription>> ("PollCompleteTranscriptions", null);

      //for each transaction in the list, download the transcript
      foreach (var t in list) {
        var blob = await context.CallActivityAsync<Uri> ("ProcessTranscript", t);
        if (blob != null && await context.CallActivityAsync<bool> ("ArchiveAudioBlob", t.RecordingsUrl)) {
          await context.CallActivityAsync<bool> ("DeleteTranscript", t);
        }
      }

      return context.InstanceId;
    }

    [FunctionName ("PollCompleteTranscriptions")]
    public static async Task<IEnumerable<Transcription>> PollCompleteTranscriptions ([ActivityTrigger] DurableActivityContext context) {
      var listUri = new Uri (baseApiUri, "/api/speechtotext/v2.0/transcriptions");
      var req = new HttpRequestMessage (HttpMethod.Get, listUri);
      req.Headers.Add ("Ocp-Apim-Subscription-Key", speechApiToken);

      IEnumerable<Transcription> list = new Collection<Transcription> ();

      try {
        var resp = await client.SendAsync (req);

        if (resp.StatusCode == HttpStatusCode.OK) {
          list = await resp.Content.ReadAsAsync<IEnumerable<Transcription>> ();
          durableLogger.LogInformation ($"{list.Count()} transcripts returned");
        } else {
          durableLogger.LogWarning ($"Transcription list failed: {resp.StatusCode.ToString()}");
        }
      } catch (HttpRequestException ex) {
        durableLogger.LogError ($"Speech API request failed", ex);
      }

      var ret = list.Where ((l) => { return String.Compare (l.Status, "succeeded", true) == 0; });
      durableLogger.LogInformation ($"{ret.Count()} transcriptions ready to be processed");

      return ret;
    }

    [FunctionName ("DeleteTranscript")]
    public static async Task<bool> DeleteTranscript ([ActivityTrigger] DurableActivityContext context) {
      var transcript = context.GetInput<Transcription> ();

      durableLogger.LogInformation ($"Deleteing transcript {transcript.Id}");

      var transcriptId = new Uri (baseApiUri, $"/api/speechtotext/v2.0/transcriptions/{transcript.Id}");
      var req = new HttpRequestMessage (HttpMethod.Delete, transcriptId);
      req.Headers.Add ("Ocp-Apim-Subscription-Key", speechApiToken);

      var resp = await client.SendAsync (req);
      return resp.StatusCode == HttpStatusCode.NoContent;
    }

    [FunctionName ("ProcessTranscript")]
    public static async Task<CloudBlockBlob> ProcessTranscript ([ActivityTrigger] DurableActivityContext context) {
      var transcript = context.GetInput<Transcription> ();

      durableLogger.LogInformation ($"Processing transcript {transcript.Id}");

      var transcripts = new List<JObject> ();

      foreach (var r in transcript.ResultsUrls) {
        var uri = new Uri (r.Value);
        transcripts.Add (JObject.Parse (await DownloadTranscript (uri)));
      }

      var acct = CloudStorageAccount.Parse (Environment.GetEnvironmentVariable ("AzureWebJobsStorage"));
      var client = acct.CreateCloudBlobClient ();
      var container = client.GetContainerReference (transcriptContainer);
      var blob = container.GetBlockBlobReference ($"{transcript.Id.ToString()}.json");

      await blob.DeleteIfExistsAsync ();

      await blob.UploadTextAsync (JsonConvert.SerializeObject (transcripts));

      durableLogger.LogInformation ($"Transcript {transcript.Id} uploaded to container {transcriptContainer}");

      return blob;
    }

    [FunctionName ("ArchiveAudioBlob")]
    public static async Task<bool> ArchiveAudioBlob ([ActivityTrigger] DurableActivityContext context) {
      var blobUri = context.GetInput<Uri> ();
      durableLogger.LogInformation ($"Archiving blob {blobUri}");

      var source = new CloudBlockBlob (blobUri);

      var acct = CloudStorageAccount.Parse (Environment.GetEnvironmentVariable ("AzureWebJobsStorage"));
      var client = acct.CreateCloudBlobClient ();
      var container = client.GetContainerReference (processedAudioContainer);
      var target = container.GetBlockBlobReference (source.Name);

      await target.DeleteIfExistsAsync ();
      var copy = await target.StartCopyAsync (source);
      var ret = false;
      if (copy == "success") ret = await source.DeleteIfExistsAsync ();

      return ret;
    }

    private static async Task<string> DownloadTranscript (Uri transcriptUri) {
      var req = new HttpRequestMessage (HttpMethod.Get, transcriptUri);
      req.Headers.Add ("Ocp-Apim-Subscription-Key", speechApiToken);

      var ret = string.Empty;

      try {
        using (var resp = await client.SendAsync (req)) {
          if (resp.StatusCode == HttpStatusCode.OK) {
            ret = await resp.Content.ReadAsStringAsync ();
          }
        }
      } catch (HttpRequestException ex) {
        durableLogger.LogError ($"Transcript: {transcriptUri}", ex);
      }

      return ret;
    }

    private static Uri GetBlobSASToken (Uri blobUri, string policyName, ILogger log) {
      var acct = CloudStorageAccount.Parse (Environment.GetEnvironmentVariable ("AzureWebJobsStorage"));
      CloudBlockBlob blob = new CloudBlockBlob (blobUri, acct.Credentials);
      var sasUri = new Uri (blobUri, blob.GetSharedAccessSignature (null, policyName));

      log.LogInformation ($"Signed Blob Uri: {sasUri.AbsoluteUri}");

      // Return the URI string for the container, including the SAS token.
      return sasUri;
    }
  }
}