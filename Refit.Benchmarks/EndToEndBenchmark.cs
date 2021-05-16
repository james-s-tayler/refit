﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Threading.Tasks;
using AutoFixture;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using RichardSzalay.MockHttp;

namespace Refit.Benchmarks
{
    [MemoryDiagnoser]
    [SimpleJob(RuntimeMoniker.NetCoreApp50)]
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    public class EndToEndBenchmark
    {
        private Fixture autoFixture = new();
        private const string Host = "https://github.com";
        private const string Url = "https://github.com/users";
        private IGitHubService systemTextJsonFixture;
        private IGitHubService newtonSoftJsonFixture;
        private User user;
        private MockHttpMessageHandler handler;
        private RefitSettings systemTextJsonRefitSettings;
        private RefitSettings newtonSoftJsonRefitSettings;
        private SystemTextJsonContentSerializer systemTextJsonContentSerializer;
        private NewtonsoftJsonContentSerializer newtonsoftJsonContentSerializer;
        private string systemTextJsonSerializedMockResponsePayload;
        private string newtonSoftJsonSerializedMockResponsePayload;
        private readonly IDictionary<int, IEnumerable<User>> users = new Dictionary<int, IEnumerable<User>>();
        private readonly IDictionary<int, string> systemTextJsonMockResponses = new Dictionary<int, string>();
        private readonly IDictionary<int, string> newtonSoftJsonMockResponses = new Dictionary<int, string>();
        private readonly IDictionary<SerializationStrategy, IGitHubService> refitClient = new Dictionary<SerializationStrategy, IGitHubService>();
        private readonly IDictionary<SerializationStrategy, IDictionary<int, string>> mockResponse = new Dictionary<SerializationStrategy, IDictionary<int, string>>
        {
            {SerializationStrategy.SystemTextJson, new Dictionary<int, string>()},
            {SerializationStrategy.NewtonsoftJson, new Dictionary<int, string>()}
        };

        private readonly IDictionary<HttpVerb, HttpMethod> httpMethod = new Dictionary<HttpVerb, HttpMethod>
        {
            {HttpVerb.Get, HttpMethod.Get}, {HttpVerb.Post, HttpMethod.Post}
        };

        private const int OneUser = 1;
        private const int TenUsers = 10;
        private const int HundredUsers = 100;
        private const int ThousandUsers = 1000;

        public enum SerializationStrategy
        {
            SystemTextJson,
            NewtonsoftJson
        }

        public enum HttpVerb
        {
            Get,
            Post
        }

        private async Task SetupDummyDataAsync(int desiredModelCount)
        {
            users[desiredModelCount] = autoFixture.CreateMany<User>(desiredModelCount);
            mockResponse[SerializationStrategy.SystemTextJson][desiredModelCount] = await systemTextJsonContentSerializer.ToHttpContent(users[desiredModelCount]).ReadAsStringAsync();
            mockResponse[SerializationStrategy.NewtonsoftJson][desiredModelCount] = await newtonsoftJsonContentSerializer.ToHttpContent(users[desiredModelCount]).ReadAsStringAsync();
        }

        private void ValidateDummyDataCount(int expectedCount)
        {
            if (users[expectedCount].Count() != expectedCount)
                throw new ArgumentOutOfRangeException($"expected {expectedCount} user(s) but got {users[expectedCount].Count()}");
        }

        private void ValidateDummyDataLength(SerializationStrategy serializer, int lowerModelCount, int higherModelCount)
        {
            if (mockResponse[serializer][lowerModelCount].Length >= mockResponse[serializer][higherModelCount].Length)
                throw new ArgumentOutOfRangeException($"expected payload of {lowerModelCount} user(s) to be shorter than that of {higherModelCount} users for {serializer}");
        }

        [GlobalSetup]
        public async Task SetupAsync()
        {
            handler = new MockHttpMessageHandler();

            systemTextJsonContentSerializer = new SystemTextJsonContentSerializer();
            systemTextJsonRefitSettings = new RefitSettings(systemTextJsonContentSerializer)
            {
                HttpMessageHandlerFactory = () => handler
            };
            refitClient[SerializationStrategy.SystemTextJson] = RestService.For<IGitHubService>(Host, systemTextJsonRefitSettings);

            newtonsoftJsonContentSerializer = new NewtonsoftJsonContentSerializer();
            newtonSoftJsonRefitSettings = new RefitSettings(newtonsoftJsonContentSerializer)
            {
                HttpMessageHandlerFactory = () => handler
            };
            refitClient[SerializationStrategy.NewtonsoftJson] = RestService.For<IGitHubService>(Host, newtonSoftJsonRefitSettings);

            await SetupDummyDataAsync(OneUser);
            await SetupDummyDataAsync(TenUsers);
            await SetupDummyDataAsync(HundredUsers);
            await SetupDummyDataAsync(ThousandUsers);

            ValidateDummyDataCount(OneUser);
            ValidateDummyDataCount(TenUsers);
            ValidateDummyDataCount(HundredUsers);
            ValidateDummyDataCount(ThousandUsers);

            ValidateDummyDataLength(SerializationStrategy.SystemTextJson, OneUser, TenUsers);
            ValidateDummyDataLength(SerializationStrategy.SystemTextJson, TenUsers, HundredUsers);
            ValidateDummyDataLength(SerializationStrategy.SystemTextJson, HundredUsers, ThousandUsers);

            ValidateDummyDataLength(SerializationStrategy.NewtonsoftJson, OneUser, TenUsers);
            ValidateDummyDataLength(SerializationStrategy.NewtonsoftJson, TenUsers, HundredUsers);
            ValidateDummyDataLength(SerializationStrategy.NewtonsoftJson, HundredUsers, ThousandUsers);
        }

        /*
         * The handler.Expect() business is these benchmarks are here because if we set the expectation in the setup method it gets garbage collected and Refit returns HTTP 404 (Not Found) during the benchmark!
         * It's the secret sauce that will help us find the holy grail... and now for something completely different!
         *
         * Each [Benchmark] tests one return type that Refit allows and is parameterized to test different payload sizes, serializers, and http methods.
         */

        [Params(200, 500)]
        public int HttpStatusCode { get; set; }

        [Params(OneUser, TenUsers, HundredUsers, ThousandUsers)]
        public int ModelCount { get; set; }

        [Params(HttpVerb.Get, HttpVerb.Post)]
        public HttpVerb Verb { get; set; }

        [Params(SerializationStrategy.SystemTextJson, SerializationStrategy.NewtonsoftJson)]
        public SerializationStrategy Serializer { get; set; }

        [Benchmark]
        public async Task Task_Async()
        {
            try
            {
                handler.Expect(httpMethod[Verb], Url).Respond((HttpStatusCode)HttpStatusCode, MediaTypeNames.Application.Json, mockResponse[Serializer][ModelCount]);
                switch (Verb)
                {
                    case HttpVerb.Get:
                        await refitClient[Serializer].GetUsersTaskAsync();
                        break;
                    case HttpVerb.Post:
                        await refitClient[Serializer].PostUsersTaskAsync(users[ModelCount]);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(Verb));
                }
            }
            catch
            {
                //swallow - Bridgekeeper: What... is the air-speed velocity of an unladen swallow?
            }
        }

        [Benchmark]
        public async Task<string> TaskString_Async()
        {
            try
            {
                handler.Expect(httpMethod[Verb], Url).Respond((HttpStatusCode)HttpStatusCode, MediaTypeNames.Application.Json, mockResponse[Serializer][ModelCount]);
                switch (Verb)
                {
                    case HttpVerb.Get:
                        return await refitClient[Serializer].GetUsersTaskStringAsync();
                    case HttpVerb.Post:
                        return await refitClient[Serializer].PostUsersTaskStringAsync(users[ModelCount]);
                    default:
                        throw new ArgumentOutOfRangeException(nameof(Verb));
                }
            }
            catch
            {
                //swallow - King Arthur: What do you mean? An African or a European swallow?
            }

            return default;
        }

        [Benchmark]
        public async Task<Stream> TaskStream_Async()
        {
            try
            {
                handler.Expect(httpMethod[Verb], Url).Respond((HttpStatusCode)HttpStatusCode, MediaTypeNames.Application.Json, mockResponse[Serializer][ModelCount]);
                switch (Verb)
                {
                    case HttpVerb.Get:
                        return await refitClient[Serializer].GetUsersTaskStreamAsync();
                    case HttpVerb.Post:
                        return await refitClient[Serializer].PostUsersTaskStreamAsync(users[ModelCount]);
                    default:
                        throw new ArgumentOutOfRangeException(nameof(Verb));
                }
            }
            catch
            {
                //swallow - Bridgekeeper: Huh? I... I don't know that. [he is thrown over by his own spell] AUUUUUUUGGGGGGGGGGGHHH!!
            }

            return default;
        }

        [Benchmark]
        public async Task<HttpContent> TaskHttpContent_Async()
        {
            try
            {
                handler.Expect(httpMethod[Verb], Url).Respond((HttpStatusCode)HttpStatusCode, MediaTypeNames.Application.Json, mockResponse[Serializer][ModelCount]);
                switch (Verb)
                {
                    case HttpVerb.Get:
                        return await refitClient[Serializer].GetUsersTaskHttpContentAsync();
                    case HttpVerb.Post:
                        return await refitClient[Serializer].PostUsersTaskHttpContentAsync(users[ModelCount]);
                    default:
                        throw new ArgumentOutOfRangeException(nameof(Verb));
                }
            }
            catch
            {
                //swallow - Sir Bedevere: How do you know so much about swallows?
            }

            return default;
        }

        [Benchmark]
        public async Task<HttpResponseMessage> TaskHttpResponseMessage_Async()
        {
            handler.Expect(httpMethod[Verb], Url).Respond((HttpStatusCode)HttpStatusCode, MediaTypeNames.Application.Json, mockResponse[Serializer][ModelCount]);
            switch (Verb)
            {
                case HttpVerb.Get:
                    return await refitClient[Serializer].GetUsersTaskHttpResponseMessageAsync();
                case HttpVerb.Post:
                    return await refitClient[Serializer].PostUsersTaskHttpResponseMessageAsync(users[ModelCount]);
                default:
                    throw new ArgumentOutOfRangeException(nameof(Verb));
            }
        }

        [Benchmark]
        public IObservable<HttpResponseMessage> ObservableHttpResponseMessage()
        {
            handler.Expect(httpMethod[Verb], Url).Respond((HttpStatusCode)HttpStatusCode, MediaTypeNames.Application.Json, mockResponse[Serializer][ModelCount]);
            switch (Verb)
            {
                case HttpVerb.Get:
                    return refitClient[Serializer].GetUsersObservableHttpResponseMessage();
                case HttpVerb.Post:
                    return refitClient[Serializer].PostUsersObservableHttpResponseMessage(users[ModelCount]);
                default:
                    throw new ArgumentOutOfRangeException(nameof(Verb));
            }
        }

        [Benchmark]
        public async Task<IEnumerable<User>> TaskT_Async()
        {
            try
            {
                handler.Expect(httpMethod[Verb], Url).Respond((HttpStatusCode)HttpStatusCode, MediaTypeNames.Application.Json, mockResponse[Serializer][ModelCount]);
                switch (Verb)
                {
                    case HttpVerb.Get:
                        return await refitClient[Serializer].GetUsersTaskTAsync();
                    case HttpVerb.Post:
                        return await refitClient[Serializer].PostUsersTaskTAsync(users[ModelCount]);
                    default:
                        throw new ArgumentOutOfRangeException(nameof(Verb));
                }
            }
            catch
            {
                //swallow - King Arthur: Well, you have to know these things when you're a king, you know.
            }

            return default;
        }

        [Benchmark]
        public async Task<ApiResponse<IEnumerable<User>>> TaskApiResponseT_Async()
        {
            handler.Expect(httpMethod[Verb], Url).Respond((HttpStatusCode)HttpStatusCode, MediaTypeNames.Application.Json, mockResponse[Serializer][ModelCount]);
            switch (Verb)
            {
                case HttpVerb.Get:
                    return await refitClient[Serializer].GetUsersTaskApiResponseTAsync();
                case HttpVerb.Post:
                    return await refitClient[Serializer].PostUsersTaskApiResponseTAsync(users[ModelCount]);
                default:
                    throw new ArgumentOutOfRangeException(nameof(Verb));
            }
        }
    }
}