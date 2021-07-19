﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using DnsTube.Dns;

namespace DnsTube
{
	public class CloudflareAPI
	{
		private Settings settings;
		private Serilog.Core.Logger log;
		public static string EndPoint = "https://api.cloudflare.com/client/v4/";
		public HttpClient Client { get; set; }

		public CloudflareAPI(HttpClient client, Settings settings, Serilog.Core.Logger log)
		{
			Client = client;
			if (Client.BaseAddress == null)
				Client.BaseAddress = new Uri(EndPoint);
			this.settings = settings;
			this.log = log;
		}

		/// <summary>
		/// Returns a list of zone IDs
		/// Ref: https://api.cloudflare.com/#zone-list-zones
		/// </summary>
		/// <returns></returns>
		public List<string> ListZoneIDs()
		{
			List<string> ret = new List<string>();
			int pageSize = 50;
			int pageNumber = 1;
			int totalPages;

			do
			{
				HttpRequestMessage req = GetRequestMessage(HttpMethod.Get, $"zones?status=active&page={pageNumber}&per_page={pageSize}&order=name&direction=asc&match=all");

				Client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

				var response = Client.SendAsync(req).Result;
				var result = response.Content.ReadAsStringAsync().Result;
				log.Information($"ListZoneIDs result: {result}");

				var validationErrors = ValidateCloudflareResult(response, result, "list zones");
				log.Information($"ListZoneIDs validationErrors: {JsonSerializer.Serialize(validationErrors)}");

				if (validationErrors.Any() || result.StartsWith("<"))
				{
					var msg = $"Error:\nResult: {result}\nValidation Errors: {string.Join("\n", validationErrors)}";
					throw new Exception(msg);
				}

				try
				{
					var zoneListResponse = JsonSerializer.Deserialize<Zone.ListZonesResponse>(result);

					int totalRecords = zoneListResponse.result_info.total_count;
					totalPages = (int)Math.Ceiling((double)totalRecords / pageSize);

					ret.AddRange(zoneListResponse.result.Select(z => z.id));
				}
				catch (Exception e)
				{
					throw new Exception($"Error: \nMessage: {e.Message} \nResult: {result}");
				}

				pageNumber++;
			} while (pageNumber <= totalPages);

			return ret;
		}

		// Ref: https://api.cloudflare.com/#dns-records-for-a-zone-list-dns-records
		private List<Result> GetRecordsByType(string zoneIdentifier, string recordType)
		{
			int pageSize = 100;
			int pageNumber = 1;
			int totalPages;

			var ret = new List<Result>();

			do
			{
				Client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

				var req = GetRequestMessage(HttpMethod.Get, $"zones/{zoneIdentifier}/dns_records?type={recordType}&page={pageNumber}&per_page={pageSize}&order=name&direction=asc&match=all");

				var response = Client.SendAsync(req).Result;
				var result = response.Content.ReadAsStringAsync().Result;
				log.Information($"GetRecordsByType result: {result}");

				var validationErrors = ValidateCloudflareResult(response, result, "list DNS records");
				log.Information($"GetRecordsByType validationErrors: {JsonSerializer.Serialize(validationErrors)}");

				if (validationErrors.Any() || result.StartsWith("<"))
				{
					var msg = $"Error:\nResult: {result}\nValidation Errors: {string.Join("\n", validationErrors)}";
					throw new Exception(msg);
				}

				try
				{
					var dnsRecordsResponse = JsonSerializer.Deserialize<DnsRecordsResponse>(result);

					int totalRecords = dnsRecordsResponse.result_info.total_count;
					totalPages = (int)Math.Ceiling((double)totalRecords / pageSize);

					ret.AddRange(dnsRecordsResponse.result);
				}
				catch (Exception e)
				{
					throw new Exception($"Error: \nMessage: {e.Message} \nResult: {result}");
				}

				pageNumber++;
			} while (pageNumber <= totalPages);

			return ret;
		}

		private List<string> ValidateCloudflareResult(HttpResponseMessage response, string result, string action)
		{
			var errors = new List<string>();
			if (!response.IsSuccessStatusCode)
			{
				var cfError = JsonSerializer.Deserialize<CloudflareApiError>(result);
				var msg = cfError.errors?.FirstOrDefault().message;
				errors.Add(msg);

				if (settings.IsUsingToken)
					errors.Add($"Unable to {action}. If you are updating all zones, token permissions should be similar to [All zones - Zone:Read, DNS:Edit]. If your token only has permissions for specific zones, click Settings and configure the Zone IDs with a comma-separated list.");
			}

			return errors;
		}

		// Ref: https://api.cloudflare.com/#dns-records-for-a-zone-update-dns-record
		public DnsUpdateResponse UpdateDns(IpSupport protocol, string zoneIdentifier, string dnsRecordIdentifier, string dnsRecordType, string dnsRecordName, string content, bool proxied)
		{
			var dnsUpdateRequest = new DnsUpdateRequest() { type = dnsRecordType, name = dnsRecordName, content = content, proxied = proxied };

			HttpResponseMessage response;

			HttpRequestMessage req = GetRequestMessage(HttpMethod.Put, $"zones/{zoneIdentifier}/dns_records/{dnsRecordIdentifier}");
			req.Content = new StringContent(JsonSerializer.Serialize(dnsUpdateRequest), Encoding.UTF8, "application/json");

			response = Client.SendAsync(req).Result;
			var result = response.Content.ReadAsStringAsync().Result;
			log.Information($"UpdateDns result: {result}");

			var validationErrors = ValidateCloudflareResult(response, result, $"update {protocol} DNS");
			log.Information($"UpdateDns validationErrors: {JsonSerializer.Serialize(validationErrors)}");

			if (validationErrors.Any() || result.StartsWith("<"))
			{
				var msg = $"Error:\nResult: {result}\nValidation Errors: {string.Join("\n", validationErrors)}";
				throw new Exception(msg);
			}

			var ret = JsonSerializer.Deserialize<DnsUpdateResponse>(result);
			return ret;
		}

		public List<Dns.Result> GetAllDnsRecordsByZone()
		{
			var zoneIDs = settings.ZoneIDs.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
			if (!zoneIDs.Any())
				zoneIDs = ListZoneIDs();

			var allDnsEntries = new List<Dns.Result>();

			foreach (var zoneID in zoneIDs)
			{
				if (settings.ProtocolSupport != IpSupport.IPv6)
				{
					var aRecords = GetRecordsByType(zoneID, "A");
					allDnsEntries.AddRange(aRecords);
				}

				if (settings.ProtocolSupport != IpSupport.IPv4)
				{
					var aaaaRecords = GetRecordsByType(zoneID, "AAAA");
					allDnsEntries.AddRange(aaaaRecords);
				}

				var txtRecords = GetRecordsByType(zoneID, "TXT");
				allDnsEntries.AddRange(txtRecords);

				var spfRecords = GetRecordsByType(zoneID, "SPF");
				allDnsEntries.AddRange(spfRecords);
			}

			return allDnsEntries.Distinct().ToList();
		}

		private HttpRequestMessage GetRequestMessage(HttpMethod httpMethod, string requestUri)
		{
			var req = new HttpRequestMessage(httpMethod, requestUri);

			if (settings.IsUsingToken)
			{
				req.Headers.Add("Authorization", " Bearer " + settings.ApiToken);
			}
			else
			{
				req.Headers.Add("X-Auth-Key", settings.ApiKey);
				req.Headers.Add("X-Auth-Email", settings.EmailAddress);
			}
			return req;
		}
	}

	public class CloudflareApiError
	{
		public bool success { get; set; }
		public Error[] errors { get; set; }
		public object[] messages { get; set; }
		public object result { get; set; }

		public class Error
		{
			public int code { get; set; }
			public string message { get; set; }
		}
	}
}
