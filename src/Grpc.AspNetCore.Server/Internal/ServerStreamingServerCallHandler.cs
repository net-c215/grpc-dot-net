﻿#region Copyright notice and license

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
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Grpc.AspNetCore.Server.Internal
{
    internal class ServerStreamingServerCallHandler<TRequest, TResponse, TService> : ServerCallHandlerBase<TRequest, TResponse, TService>
        where TRequest : class
        where TResponse : class
        where TService : class
    {
        // We're using an open delegate (the first argument is the TService instance) to represent the call here since the instance is create per request.
        // This is the reason we're not using the delegates defined in Grpc.Core. This delegate maps to ServerStreamingServerMethod<TRequest, TResponse>
        // with an instance parameter.
        private delegate Task ServerStreamingServerMethod(TService service, TRequest request, IServerStreamWriter<TResponse> stream, ServerCallContext serverCallContext);

        private readonly ServerStreamingServerMethod _invoker;

        public ServerStreamingServerCallHandler(Method<TRequest, TResponse> method) : base(method)
        {
            var handlerMethod = typeof(TService).GetMethod(Method.Name);

            _invoker = (ServerStreamingServerMethod)Delegate.CreateDelegate(typeof(ServerStreamingServerMethod), handlerMethod);
        }

        public override async Task HandleCallAsync(HttpContext httpContext)
        {
            httpContext.Response.ContentType = "application/grpc";
            httpContext.Response.Headers.Append("grpc-encoding", "identity");

            var requestPayload = await StreamUtils.ReadMessageAsync(httpContext.Request.Body);
            // TODO(JunTaoLuo, JamesNK): make sure the payload is not null
            var request = Method.RequestMarshaller.Deserializer(requestPayload);

            // TODO(JunTaoLuo, JamesNK): make sure there are no more request messages.

            // Activate the implementation type via DI.
            var activator = httpContext.RequestServices.GetRequiredService<IGrpcServiceActivator<TService>>();
            var service = activator.Create();

            await _invoker(service, request, new HttpContextStreamWriter<TResponse>(httpContext, Method.ResponseMarshaller.Serializer), null);

            httpContext.Response.AppendTrailer(GrpcProtocolConstants.StatusTrailer, GrpcProtocolConstants.StatusOk);
        }
    }
}
