﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Owin;
using OwinFramework.Interfaces.Builder;
using OwinFramework.Interfaces.Routing;

namespace OwinFramework.Routing
{
    /// <summary>
    /// A router consists of a set of filter expressions and a segment (list of middleware) 
    /// to execute when that filter evaluates to true. When routing and processing
    /// requests the router will evaluate filters until one matches, then only
    /// execute that one segment.
    /// </summary>
    public class Router : IRouter
    {
        public string Name { get; set; }
        public IList<IDependency> Dependencies { get; private set; }
        public IList<IRoutingSegment> Segments { get; private set; }

        private readonly string _owinContextKey;
        private readonly IDependencyTreeFactory _dependencyTreeFactory;

        public Router(IDependencyTreeFactory dependencyTreeFactory)
        {
            _dependencyTreeFactory = dependencyTreeFactory;

            _owinContextKey = "R:" + Guid.NewGuid().ToString("N");
            Dependencies = new List<IDependency>();
            Segments = new List<IRoutingSegment>();
        }

        public IRouter Add(string routeName, Func<IOwinContext, bool> filterExpression)
        {
            Segments.Add(new RoutingSegment(_dependencyTreeFactory).Initialize(routeName, filterExpression));
            return this;
        }

        public void RouteRequest(IOwinContext context, Action next)
        {
            foreach (var segment in Segments)
            {
                if (segment.Filter(context))
                {
                    context.Set(_owinContextKey, segment);
                    segment.RouteRequest(context, next);
                    return;
                }
            }
            next();
        }

        public Task Invoke(IOwinContext context, Func<Task> next)
        {
            var segment = context.Get<IRoutingSegment>(_owinContextKey);

            if (segment == null)
                return next();

            return segment.Invoke(context, next);
        }

        /// <summary>
        /// This is used to encapsulate information about the middleware components
        /// during the process of resolving dependencies
        /// </summary>
        private class Component
        {
            public IMiddleware Middleware;
            public Type MiddlewareType;
            public string Name;
            public IList<IDependency> Dependencies;
        }

        /// <summary>
        /// The routing segment represents an ordered list of middleware components
        /// that are chained together and executed in a pipeline. The Router will 
        /// use logic to select which RoutingSegment to execute for a given request.
        /// </summary>
        private class RoutingSegment : IRoutingSegment
        {
            public string Name { get; private set; }
            public Func<IOwinContext, bool> Filter { get; private set; }

            private readonly IList<Component> _components;
            private readonly IDependencyTreeFactory _dependencyTreeFactory;

            private IList<IMiddleware> _middleware;
            private IList<IRoutingProcessor> _routingProcessors;

            public RoutingSegment(IDependencyTreeFactory dependencyTreeFactory)
            {
                _dependencyTreeFactory = dependencyTreeFactory;
                _components = new List<Component>();
            }

            public IRoutingSegment Initialize(string name, Func<IOwinContext, bool> filter)
            {
                Name = name;
                Filter = filter;
                return this;
            }

            public void Add(IMiddleware middleware, Type middlewareType)
            {
                if (middleware != null)
                {
                    _components.Add(new Component
                    {
                        Middleware = middleware,
                        MiddlewareType = middlewareType
                    });
                }
            }

            public void ResolveDependencies()
            {
                // Get information about the components - their properties might have
                // changed since they were registered
                foreach (var component in _components)
                {
                    component.Name = component.Middleware.Name;
                    component.Dependencies = component.Middleware.Dependencies;
                }

                // When there are multiple middleware components implementing the same
                // interface then dependencies must use the fully qualifies reference.
                // When there is only one instance the names should be ignored to 
                // simplify configuration
                var singeltons = new List<Type>();
                var multiples = new List<Type>();
                foreach (var component in _components)
                {
                    if (!multiples.Contains(component.MiddlewareType))
                    {
                        if (singeltons.Contains(component.MiddlewareType))
                        {
                            multiples.Add(component.MiddlewareType);
                            singeltons.Remove(component.MiddlewareType);
                        }
                        else
                            singeltons.Add(component.MiddlewareType);
                    }
                }

                // These keys are used to pass dependencies to the dependency graph 
                Func<Type, string, string> buildKey = (t, n) =>
                {
                    var key = t.FullName;
                    if (!string.IsNullOrEmpty(n) && !singeltons.Contains(t))
                        key += ":" + n.ToLower();
                    return key;
                };

                // Build a dependency graph
                var dependencyTree = _dependencyTreeFactory.Create<string, Component>();
                foreach (var component in _components)
                {
                    var key = buildKey(component.MiddlewareType, component.Name);
                    var dependentKeys = component.Dependencies == null
                        ? null
                        : component
                            .Dependencies
                            .Select(c => buildKey(c.DependentType, c.Name));

                    dependencyTree.Add(key, component, dependentKeys);
                }

                // Sort components by order of least to most dependent
                var orderedComponents = dependencyTree.GetAllData();

                _middleware = orderedComponents
                    .Select(c => c.Middleware)
                    .ToList();

                // Make a list of the middleware that wants to participate in routing
                // so that we don't figure this out again for each request
                _routingProcessors = _middleware
                    .Select(middleware => middleware as IRoutingProcessor)
                    .Where(rp => rp != null)
                    .ToList();
            }

            public void RouteRequest(IOwinContext context, Action next)
            {
                if (_routingProcessors == null)
                    throw new RoutingException("Requests can not be routed until dependencies have been resolved");

                var nextIndex = 0;
                Action nextAction = null;

                nextAction = () =>
                {
                    if (nextIndex < _routingProcessors.Count)
                        _routingProcessors[nextIndex++].RouteRequest(context, nextAction);
                    else
                        next();
                };

                nextAction();
            }

            public Task Invoke(IOwinContext context, Func<Task> next)
            {
                if (_middleware == null)
                    throw new RoutingException("Requests can not be processed until dependencies have been resolved");

                var nextIndex = 0;
                Func<Task> getNext = null;

                getNext = () =>
                    {
                        if (nextIndex < _middleware.Count)
                            return _middleware[nextIndex++].Invoke(context, getNext);
                        return next();
                    };

                return getNext();
            }
        }
    }
}
