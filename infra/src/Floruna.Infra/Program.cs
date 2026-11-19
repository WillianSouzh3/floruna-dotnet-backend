using Amazon.CDK;
using Floruna.Infra;

// cdk synth -c env=staging -c imageTag=<sha>
// cdk deploy --all -c env=prod -c imageTag=<sha> -c certificateArn=arn:aws:acm:...
var app = new App();
FlorunaApp.Build(app, FlorunaEnvironment.FromContext(key => app.Node.TryGetContext(key) as string), app.Node.TryGetContext("githubRepo") as string);
app.Synth();
