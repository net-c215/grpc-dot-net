#region Copyright notice and license

// Copyright 2019 The gRPC Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#endregion

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using FunctionalTestsWebsite;
using FunctionalTestsWebsite.Services;
using Google.Protobuf.WellKnownTypes;
using Greet;
using Grpc.AspNetCore.FunctionalTests.Infrastructure;
using Grpc.AspNetCore.Server.Internal;
using Grpc.AspNetCore.Server.Tests;
using Grpc.Core;
using NUnit.Framework;

namespace Grpc.AspNetCore.FunctionalTests
{
    [TestFixture]
    public class UnaryMethodTests : FunctionalTestBase
    {
        [Test]
        public async Task SendValidRequest_SuccessResponse()
        {
            // Arrange
            var requestMessage = new HelloRequest
            {
                Name = "World"
            };

            var ms = new MemoryStream();
            MessageHelpers.WriteMessage(ms, requestMessage);

            // Act
            var response = await Fixture.Client.PostAsync(
                "Greet.Greeter/SayHello",
                new GrpcStreamContent(ms)).DefaultTimeout();

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("identity", response.Headers.GetValues("grpc-encoding").Single());
            Assert.AreEqual("application/grpc", response.Content.Headers.ContentType.MediaType);

            var responseMessage = MessageHelpers.AssertReadMessage<HelloReply>(await response.Content.ReadAsByteArrayAsync().DefaultTimeout());
            Assert.AreEqual("Hello World", responseMessage.Message);

            Assert.AreEqual(StatusCode.OK.ToTrailerString(), Fixture.TrailersContainer.Trailers[GrpcProtocolConstants.StatusTrailer].Single());
        }

        [Test]
        public async Task StreamedMessage_SuccessResponseAfterMessageReceived()
        {
            // Arrange
            var requestMessage = new HelloRequest
            {
                Name = "World"
            };

            var ms = new MemoryStream();
            MessageHelpers.WriteMessage(ms, requestMessage);

            var requestStream = new SyncPointMemoryStream();

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "Greet.Greeter/SayHello");
            httpRequest.Content = new GrpcStreamContent(requestStream);

            // Act
            var responseTask = Fixture.Client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);

            // Assert
            Assert.IsFalse(responseTask.IsCompleted, "Server should wait for client to finish streaming");

            await requestStream.AddDataAndWait(ms.ToArray()).DefaultTimeout();
            await requestStream.AddDataAndWait(Array.Empty<byte>()).DefaultTimeout();

            var response = await responseTask.DefaultTimeout();

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("identity", response.Headers.GetValues("grpc-encoding").Single());
            Assert.AreEqual("application/grpc", response.Content.Headers.ContentType.MediaType);

            var responseMessage = MessageHelpers.AssertReadMessage<HelloReply>(await response.Content.ReadAsByteArrayAsync().DefaultTimeout());
            Assert.AreEqual("Hello World", responseMessage.Message);
        }

        [Test]
        public async Task AdditionalDataAfterStreamedMessage_ErrorResponse()
        {
            // Arrange
            SetExpectedErrorsFilter(writeContext =>
            {
                return writeContext.LoggerName == typeof(GreeterService).FullName &&
                       writeContext.EventId.Name == "RpcConnectionError" &&
                       writeContext.State.ToString() == "Error status code 'Internal' raised.";
            });

            var requestMessage = new HelloRequest
            {
                Name = "World"
            };

            var ms = new MemoryStream();
            MessageHelpers.WriteMessage(ms, requestMessage);

            var requestStream = new SyncPointMemoryStream();

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "Greet.Greeter/SayHello");
            httpRequest.Content = new GrpcStreamContent(requestStream);

            // Act
            var responseTask = Fixture.Client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);

            // Assert
            Assert.IsFalse(responseTask.IsCompleted, "Server should wait for client to finish streaming");

            await requestStream.AddDataAndWait(ms.ToArray()).DefaultTimeout();
            await requestStream.AddDataAndWait(ms.ToArray()).DefaultTimeout();

            await responseTask.DefaultTimeout();

            Assert.AreEqual(StatusCode.Internal.ToTrailerString(), Fixture.TrailersContainer.Trailers[GrpcProtocolConstants.StatusTrailer].Single());
            Assert.AreEqual("Additional data after the message received.", Fixture.TrailersContainer.Trailers[GrpcProtocolConstants.MessageTrailer].Single());
        }

        [Test]
        public async Task MessageSentInMultipleChunks_SuccessResponse()
        {
            // Arrange
            SetExpectedErrorsFilter(writeContext =>
            {
                return writeContext.LoggerName == typeof(GreeterService).FullName &&
                       writeContext.EventId.Name == "RpcConnectionError" &&
                       writeContext.State.ToString() == "Error status code 'Internal' raised.";
            });

            var requestMessage = new HelloRequest
            {
                Name = "World"
            };

            var ms = new MemoryStream();
            MessageHelpers.WriteMessage(ms, requestMessage);

            var requestStream = new SyncPointMemoryStream();

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "Greet.Greeter/SayHello");
            httpRequest.Content = new GrpcStreamContent(requestStream);

            // Act
            var responseTask = Fixture.Client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);

            // Assert
            Assert.IsFalse(responseTask.IsCompleted, "Server should wait for client to finish streaming");

            // Send message one byte at a time then finish
            foreach (var b in ms.ToArray())
            {
                await requestStream.AddDataAndWait(new[] { b }).DefaultTimeout();
            }
            await requestStream.AddDataAndWait(Array.Empty<byte>()).DefaultTimeout();

            var response = await responseTask.DefaultTimeout();

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            var responseMessage = MessageHelpers.AssertReadMessage<HelloReply>(await response.Content.ReadAsByteArrayAsync().DefaultTimeout());
            Assert.AreEqual("Hello World", responseMessage.Message);

            Assert.AreEqual(StatusCode.OK.ToTrailerString(), Fixture.TrailersContainer.Trailers[GrpcProtocolConstants.StatusTrailer].Single());
        }

        [Test]
        public async Task SendHeadersTwice_ThrowsException()
        {
            static async Task<HelloReply> ReturnHeadersTwice(HelloRequest request, ServerCallContext context)
            {
                await context.WriteResponseHeadersAsync(null);

                await context.WriteResponseHeadersAsync(null);

                return new HelloReply { Message = "Should never reach here" };
            }

            // Arrange
            SetExpectedErrorsFilter(writeContext =>
            {
                return writeContext.LoggerName == typeof(UnaryMethodTests).FullName &&
                       writeContext.EventId.Name == "ErrorExecutingServiceMethod" &&
                       writeContext.State.ToString() == "Error when executing service method 'ReturnHeadersTwice'." &&
                       writeContext.Exception.Message == "Response headers can only be sent once per call.";
            });

            var requestMessage = new HelloRequest
            {
                Name = "World"
            };

            var ms = new MemoryStream();
            MessageHelpers.WriteMessage(ms, requestMessage);

            var url = Fixture.DynamicGrpc.AddUnaryMethod<UnaryMethodTests, HelloRequest, HelloReply>(ReturnHeadersTwice, nameof(ReturnHeadersTwice));

            // Act
            var response = await Fixture.Client.PostAsync(
                url,
                new GrpcStreamContent(ms)).DefaultTimeout();

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("identity", response.Headers.GetValues("grpc-encoding").Single());
            Assert.AreEqual("application/grpc", response.Content.Headers.ContentType.MediaType);

            Assert.AreEqual(StatusCode.Unknown.ToTrailerString(), Fixture.TrailersContainer.Trailers[GrpcProtocolConstants.StatusTrailer].Single());
            Assert.AreEqual("Exception was thrown by handler. InvalidOperationException: Response headers can only be sent once per call.", Fixture.TrailersContainer.Trailers[GrpcProtocolConstants.MessageTrailer].Single());
        }

        [Test]
        public async Task ServerMethodReturnsNull_FailureResponse()
        {
            // Arrange
            SetExpectedErrorsFilter(writeContext =>
            {
                return writeContext.LoggerName == typeof(GreeterService).FullName &&
                       writeContext.EventId.Name == "RpcConnectionError" &&
                       writeContext.State.ToString() == "Error status code 'Cancelled' raised." &&
                       GetRpcExceptionDetail(writeContext.Exception) == "No message returned from method.";
            });

            var requestMessage = new HelloRequest
            {
                Name = "World"
            };

            var ms = new MemoryStream();
            MessageHelpers.WriteMessage(ms, requestMessage);

            // Act
            var response = await Fixture.Client.PostAsync(
                "Greet.Greeter/SayHelloReturnNull",
                new GrpcStreamContent(ms)).DefaultTimeout();

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            Assert.AreEqual(StatusCode.Cancelled.ToTrailerString(), Fixture.TrailersContainer.Trailers[GrpcProtocolConstants.StatusTrailer].Single());
            Assert.AreEqual("No message returned from method.", Fixture.TrailersContainer.Trailers[GrpcProtocolConstants.MessageTrailer].Single());
        }

        [Test]
        public async Task ServerMethodThrowsExceptionWithTrailers_FailureResponse()
        {
            // Arrange
            SetExpectedErrorsFilter(writeContext =>
            {
                return writeContext.LoggerName == typeof(GreeterService).FullName &&
                       writeContext.EventId.Name == "RpcConnectionError" &&
                       writeContext.State.ToString() == "Error status code 'Unknown' raised." &&
                       GetRpcExceptionDetail(writeContext.Exception) == "User error";
            });

            var requestMessage = new HelloRequest
            {
                Name = "World"
            };

            var ms = new MemoryStream();
            MessageHelpers.WriteMessage(ms, requestMessage);

            // Act
            var response = await Fixture.Client.PostAsync(
                "Greet.Greeter/SayHelloThrowExceptionWithTrailers",
                new GrpcStreamContent(ms)).DefaultTimeout();

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            Assert.AreEqual(StatusCode.Unknown.ToTrailerString(), Fixture.TrailersContainer.Trailers[GrpcProtocolConstants.StatusTrailer].Single());
            Assert.AreEqual("User error", Fixture.TrailersContainer.Trailers[GrpcProtocolConstants.MessageTrailer].Single());
            Assert.AreEqual("A value!", Fixture.TrailersContainer.Trailers["test-trailer"].Single());
        }

        [Test]
        public async Task ValidRequest_ReturnContextInfoInTrailers()
        {
            static Task<Empty> ReturnContextInfoInTrailers(Empty request, ServerCallContext context)
            {
                context.ResponseTrailers.Add("Test-Method", context.Method);
                context.ResponseTrailers.Add("Test-Peer", context.Peer ?? string.Empty); // null because there is not a remote ip address
                context.ResponseTrailers.Add("Test-Host", context.Host);

                return Task.FromResult(new Empty());
            }

            // Arrange
            var requestMessage = new Empty();

            var ms = new MemoryStream();
            MessageHelpers.WriteMessage(ms, requestMessage);

            var url = Fixture.DynamicGrpc.AddUnaryMethod<UnaryMethodTests, Empty, Empty>(ReturnContextInfoInTrailers);

            // Act
            var response = await Fixture.Client.PostAsync(
                url,
                new GrpcStreamContent(ms)).DefaultTimeout();

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("identity", response.Headers.GetValues("grpc-encoding").Single());
            Assert.AreEqual("application/grpc", response.Content.Headers.ContentType.MediaType);

            var responseMessage = MessageHelpers.AssertReadMessage<Empty>(await response.Content.ReadAsByteArrayAsync().DefaultTimeout());
            Assert.IsNotNull(responseMessage);

            var methodParts = Fixture.TrailersContainer.Trailers["Test-Method"].ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
            var serviceName = methodParts[0];
            var methodName = methodParts[1];

            Assert.AreEqual("UnaryMethodTests", serviceName);
            Assert.IsTrue(Guid.TryParse(methodName, out var _));

            Assert.AreEqual(string.Empty, Fixture.TrailersContainer.Trailers["Test-Peer"].ToString());
            Assert.AreEqual("localhost", Fixture.TrailersContainer.Trailers["Test-Host"].ToString());
        }

        [TestCase(null, "Content-Type is missing from the request.")]
        [TestCase("application/json", "Content-Type 'application/json' is not supported.")]
        [TestCase("application/binary", "Content-Type 'application/binary' is not supported.")]
        [TestCase("application/grpc-web", "Content-Type 'application/grpc-web' is not supported.")]
        public async Task InvalidContentType_Return415Response(string contentType, string responseMessage)
        {
            // Arrange
            var requestMessage = new HelloRequest
            {
                Name = "World"
            };

            var ms = new MemoryStream();
            MessageHelpers.WriteMessage(ms, requestMessage);
            var streamContent = new StreamContent(ms);
            streamContent.Headers.ContentType = contentType != null ? new MediaTypeHeaderValue(contentType) : null;

            // Act
            var response = await Fixture.Client.PostAsync(
                "Greet.Greeter/SayHello",
                streamContent).DefaultTimeout();

            // Assert
            Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

            var content = await response.Content.ReadAsStringAsync().DefaultTimeout();
            Assert.AreEqual(responseMessage, content);

            Assert.AreEqual(StatusCode.Internal.ToTrailerString(), Fixture.TrailersContainer.Trailers[GrpcProtocolConstants.StatusTrailer].Single());
            Assert.AreEqual(responseMessage, Fixture.TrailersContainer.Trailers[GrpcProtocolConstants.MessageTrailer].Single());
        }

        [TestCase("application/grpc")]
        [TestCase("APPLICATION/GRPC")]
        [TestCase("application/grpc+proto")]
        [TestCase("APPLICATION/GRPC+PROTO")]
        [TestCase("application/grpc+json")] // Accept any message format. A Method+marshaller may have been set that reads and writes JSON
        [TestCase("application/grpc; param=one")]
        public async Task ValidContentType_ReturnValidResponse(string contentType)
        {
            // Arrange
            var requestMessage = new HelloRequest
            {
                Name = "World"
            };

            var ms = new MemoryStream();
            MessageHelpers.WriteMessage(ms, requestMessage);
            var streamContent = new StreamContent(ms);
            streamContent.Headers.ContentType = contentType != null ? MediaTypeHeaderValue.Parse(contentType) : null;

            // Act
            var response = await Fixture.Client.PostAsync(
                "Greet.Greeter/SayHello",
                streamContent).DefaultTimeout();

            // Assert
            var responseMessage = MessageHelpers.AssertReadMessage<HelloReply>(await response.Content.ReadAsByteArrayAsync().DefaultTimeout());
            Assert.AreEqual("Hello World", responseMessage.Message);

            Assert.AreEqual(StatusCode.OK.ToTrailerString(), Fixture.TrailersContainer.Trailers[GrpcProtocolConstants.StatusTrailer].Single());
        }
    }
}
