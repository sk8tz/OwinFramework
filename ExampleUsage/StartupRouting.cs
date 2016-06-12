﻿using ExampleUsage.Middleware;
using Owin;
using OwinFramework;
using OwinFramework.Builder;
using OwinFramework.Interfaces;
using OwinFramework.Interfaces.Routing;
using OwinFramework.Routing;
using OwinFramework.Utility;

namespace ExampleUsage
{
    /// <summary>
    /// This example demonstrates a a more complex scenario with routes and
    /// different authentication mechanisms for each route. The resulting
    /// OWIN pipeline will be built like this:
    /// 
    /// session --> aspx pages --ui---> /secure --SecureUI--> forms auth -----> template rendering
    ///          |                  |                                        |
    ///          |                  --> not /secure --PublicUI---------------^
    ///          |
    ///          -> non aspx --api--> cert auth ----> REST service rendering
    /// </summary>
    public class StartupRouting
    {
        public void Configuration(IAppBuilder app)
        {
            // Don't do object construction like this in your application, use IoC instead.
            var dependencyTreeFactory = new DependencyTreeFactory();
            var builder = new Builder(dependencyTreeFactory);
            var configuration = new DefaultValueConfiguration();

            // Note that the middleware components below can be registerd with the builder
            // in ant order. The builder will resolve dependencies and add middleware into 
            // the OWIN pipeline so that all dependencies are satisfied. If there are
            // circular dependencies an exception will be thrown.

            // These middleware are configured to run at the front and back of the OWIN
            // pipeline so not much to configure here.
            builder.Register(new NotFoundError());
            builder.Register(new ReportExceptions());

            // This says that we want to use forms based identification, that
            // we will refer to it by the name 'forms', and it will
            // only be configured for the 'secure' route.
            builder.Register(new FormsIdentification())
                .As("forms")
                .ConfigureWith(configuration, "/owin/auth/forms")
                .RunAfter<IRoute>("secure");

            // This says that we want to use certificate based identification, that
            // we will refer to it by the name 'cert', and it will
            // only be configured for the 'api' route.
            builder.Register(new CertificateIdentification())
                .As("cert")
                .ConfigureWith(configuration, "/owin/auth/cert")
                .RunAfter<IRoute>("api");

            // This specifies the mechanism we want to use to store session
            builder.Register(new InProcessSession())
                .RunAfter<IIdentification>("forms")
                .ConfigureWith(configuration, "/owin/session");

            // This configures a routing element that will split the OWIN pipeline into
            // two routes. There is a 'UI' route that has forms based authentication and 
            // template based rendering. There is an 'API' route that has certifcate based
            // authentication and REST service rendering. It also specifies that routing
            // runs after session, so both routes will use the same session middleware. If
            // you condifure more than one session middleware in this scenario then an exception
            // will be thrown at startup.
            builder.Register(new Router(dependencyTreeFactory))
                .AddRoute("ui", context => context.Request.Path.Value.EndsWith(".aspx"))
                .AddRoute("api", context => true)
                .RunAfter<ISession>(null, false);

            // This configures another routing split that divides the 'UI' route into secure
            // and public routes called 'SecureUI' and 'PublicUI'.
            builder.Register(new Router(dependencyTreeFactory))
                .AddRoute("secure", context => context.Request.Path.Value.StartsWith("/secure"))
                .AddRoute("public", context => true)
                .RunAfter<IRoute>("ui");

            // This specifies that we want to use the template page rendering
            // middleware and that it should run on both the "public" route and
            // the "secure" route. This joins two of the routes back together.
            builder.Register(new TemplatePageRendering())
                .RunAfter<IRoute>("public")
                .RunAfter<IRoute>("secure")
                .ConfigureWith(configuration, "/owin/templates");

            // This specifies that we want to use the REST service mapper
            // middleware and that it should run after IIdentification middleware called 'cert'
            // Note that we could also have told it to RunAfter<IRoute>("API") and the
            // resulting OWIN pipeline would be the same. For belts and braces we could
            // also add both dependencies.
            builder.Register(new RestServiceMapper())
                .RunAfter<IIdentification>("cert")
                .ConfigureWith(configuration, "/owin/rest");

            // This statement will add all of the middleware registered with the builder into
            // the OWIN pipeline. The builder will add middleware to the pipeline in an order
            // that ensures all dependencies are met. The builder will also create splits in the
            // OWIN pipeline where there are routing components configured, and joins where
            // middleware has a dependency on multiple routes.
            app.UseBuilder(builder);

            app.UseErrorPage();
            app.UseWelcomePage("/");
        }
    }
}