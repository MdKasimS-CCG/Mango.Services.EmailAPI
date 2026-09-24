This is Email API for Mango application

## Docker Setup Commands - Can Be Used Via Any Terminal

Building the image:
docker build --secret id=nugetconfig,src="$env:NUGET_CONFIG_PATH" -t mango-email-local:dev .

Running the container:
docker run --name mango-emailapi --env-file .env -p 5126:8080 imagename:dev

Note: Just replace the image name with what is shown on your docker desktop
